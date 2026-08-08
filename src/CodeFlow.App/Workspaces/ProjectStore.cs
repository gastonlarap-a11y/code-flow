using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Workspaces;

/// <summary>
/// Project rows and the queries behind them.
/// </summary>
/// <remarks>
/// A project is one repository registered under a workspace. See
/// <c>docs/business-rules/09-workspace-scoped.md</c>.
/// </remarks>
internal static class ProjectStore
{
    private const string Columns =
        """
        id, workspace_id, name, local_path, remote_url, color, icon,
        ado_org, ado_project, ado_repo_id, github_owner, github_repo, github_host,
        sort_order, created_at
        """;

    public static Project Create(SqliteConnection connection, NewProject input)
    {
        var project = new Project(
            Guid.NewGuid().ToString(),
            input.WorkspaceId,
            input.Name,
            input.LocalPath,
            input.RemoteUrl,
            input.Color,
            input.Icon,
            input.AdoOrg,
            input.AdoProject,
            input.AdoRepoId,
            input.GithubOwner,
            input.GithubRepo,
            input.GithubHost,
            SortOrder: 0,
            Clock.Now());

        Sql.Execute(connection,
            """
            INSERT INTO projects (
                id, workspace_id, name, local_path, remote_url, color, icon,
                ado_org, ado_project, ado_repo_id, github_owner, github_repo, github_host,
                sort_order, created_at)
            VALUES (
                $id, $workspaceId, $name, $localPath, $remoteUrl, $color, $icon,
                $adoOrg, $adoProject, $adoRepoId, $githubOwner, $githubRepo, $githubHost,
                $sortOrder, $createdAt)
            """,
            ("$id", project.Id),
            ("$workspaceId", project.WorkspaceId),
            ("$name", project.Name),
            ("$localPath", project.LocalPath),
            ("$remoteUrl", project.RemoteUrl),
            ("$color", project.Color),
            ("$icon", project.Icon),
            ("$adoOrg", project.AdoOrg),
            ("$adoProject", project.AdoProject),
            ("$adoRepoId", project.AdoRepoId),
            ("$githubOwner", project.GithubOwner),
            ("$githubRepo", project.GithubRepo),
            ("$githubHost", project.GithubHost),
            ("$sortOrder", project.SortOrder),
            ("$createdAt", project.CreatedAt));

        return project;
    }

    public static List<Project> List(SqliteConnection connection, string workspaceId) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM projects WHERE workspace_id = $workspaceId ORDER BY sort_order, created_at",
            Read,
            ("$workspaceId", workspaceId));

    /// <summary>Every project in every workspace, for the flows that search across all of them.</summary>
    /// <remarks>
    /// Ordered by <c>created_at</c> like the per-workspace list, which is what makes "the first project
    /// that matches" a stable answer rather than whatever SQLite returned first.
    /// </remarks>
    public static List<Project> All(SqliteConnection connection) =>
        Sql.Query(connection, $"SELECT {Columns} FROM projects ORDER BY created_at", Read);

    public static Project? Get(SqliteConnection connection, string id) =>
        Sql.QuerySingle(connection, $"SELECT {Columns} FROM projects WHERE id = $id", Read, ("$id", id));

    /// <inheritdoc cref="WorkspaceStore.Delete"/>
    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM projects WHERE id = $id", ("$id", id));

    /// <summary>
    /// Reparents a project, which retroactively changes which configuration applies to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <c>UPDATE</c>, exactly as 1.7.2. Review contexts, the agent roster, MCP servers,
    /// skills and the prompt overrides are all resolved from a project's <em>current</em>
    /// <c>workspace_id</c> at the moment an AI action runs, so this swaps the destination
    /// workspace's whole configuration in for every subsequent action (<c>WS-003</c>).
    /// </para>
    /// <para>
    /// <c>review_runs.workspace_id</c> moves with the project — this closed <c>BUG-STORE-b</c>.
    /// It is a write-time denormalisation with no foreign key, so nothing else keeps it true;
    /// 1.7.2 left it stale and a moved project's review history dropped out of its new
    /// workspace's list. Both statements run inside the caller's transaction
    /// (<c>Database.WriteAsync</c>), so a crash between them cannot strand the copy. A
    /// migration step backfills databases that diverged before the fix.
    /// </para>
    /// <para>
    /// A destination that does not exist makes this throw: the connection runs with
    /// <c>PRAGMA foreign_keys = ON</c>, so the foreign key on <c>projects.workspace_id</c> rejects
    /// the statement. That resolves <c>AMBIGUOUS-WS-a</c>, which could not be settled from
    /// the SQL alone. CodeFlow 1.7.2 set the same pragma on its own single long-lived connection,
    /// so behaviour is identical.
    /// </para>
    /// </remarks>
    public static void MoveToWorkspace(SqliteConnection connection, string id, string workspaceId)
    {
        Sql.Execute(connection, "UPDATE projects SET workspace_id = $workspaceId WHERE id = $id",
            ("$id", id), ("$workspaceId", workspaceId));
        Sql.Execute(connection, "UPDATE review_runs SET workspace_id = $workspaceId WHERE project_id = $id",
            ("$id", id), ("$workspaceId", workspaceId));
    }

    public static void UpdateColor(SqliteConnection connection, string id, string color) =>
        Sql.Execute(connection, "UPDATE projects SET color = $color WHERE id = $id",
            ("$id", id), ("$color", color));

    /// <summary>Points a project at a GitHub repository.</summary>
    /// <remarks>
    /// Writes <b>only</b> the three GitHub columns and leaves any Azure ones standing — which is why
    /// every caller that re-links calls <see cref="Unlink"/> first. A project carrying both sets
    /// dispatches to GitHub, so a stale Azure pair left behind would be invisible rather than harmless.
    /// </remarks>
    public static void LinkGithub(
        SqliteConnection connection, string id, string owner, string repo, string host) =>
        Sql.Execute(connection,
            "UPDATE projects SET github_owner = $owner, github_repo = $repo, github_host = $host WHERE id = $id",
            ("$owner", owner), ("$repo", repo), ("$host", host), ("$id", id));

    /// <summary>Points a project at an Azure DevOps repository.</summary>
    /// <remarks>
    /// The mirror of <see cref="LinkGithub"/>, with the same one-sided behaviour. <paramref name="repoId"/>
    /// holds a repository name in practice — Azure's Git API accepts a name wherever it accepts a GUID.
    /// </remarks>
    public static void LinkAdo(
        SqliteConnection connection, string id, string org, string project, string repoId) =>
        Sql.Execute(connection,
            "UPDATE projects SET ado_org = $org, ado_project = $project, ado_repo_id = $repoId WHERE id = $id",
            ("$org", org), ("$project", project), ("$repoId", repoId), ("$id", id));

    /// <summary>Clears every VCS link on a project.</summary>
    /// <remarks>
    /// All six columns, unconditionally, without looking at which were set: a project is linked to at
    /// most one host, so "disconnect" wipes whichever it was and the caller never has to know which
    /// provider it is undoing.
    /// </remarks>
    public static void Unlink(SqliteConnection connection, string id) =>
        Sql.Execute(connection,
            "UPDATE projects SET ado_org = NULL, ado_project = NULL, ado_repo_id = NULL, "
            + "github_owner = NULL, github_repo = NULL, github_host = NULL WHERE id = $id",
            ("$id", id));

    private static Project Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.TextOrNull(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.TextOrNull(7),
        reader.TextOrNull(8),
        reader.TextOrNull(9),
        reader.TextOrNull(10),
        reader.TextOrNull(11),
        reader.TextOrNull(12),
        reader.GetInt64(13),
        reader.GetString(14));
}
