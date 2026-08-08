using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Workspaces;

/// <summary>
/// The <c>workspace_skills</c> rows.
/// See <c>docs/business-rules/09-workspace-scoped.md</c> §"Skills subsystem".
/// </summary>
/// <remarks>
/// A skill is a row here <em>and</em> a folder under
/// <see cref="Platform.AppPaths.WorkspaceSkillsDirectory"/>; the two are tied together by name
/// alone, never by an id or a marker file. That choice is what <see cref="SkillSync"/> depends on,
/// and it is also what makes <c>BUG-WS-a</c> and <c>BUG-WS-b</c> possible.
/// </remarks>
internal static class SkillStore
{
    private const string Columns = "id, workspace_id, skill_name, source_repo, enabled, installed_at";

    public static List<WorkspaceSkill> List(SqliteConnection connection, string workspaceId) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM workspace_skills WHERE workspace_id = $workspaceId ORDER BY installed_at",
            Read,
            ("$workspaceId", workspaceId));

    /// <summary>Records a newly installed skill.</summary>
    /// <remarks>
    /// <b><c>BUG-WS-b</c>, reproduced.</b> There is no check here for a skill of the same name, and
    /// the table has no <c>UNIQUE(workspace_id, skill_name)</c> — so installing the same skill twice
    /// leaves two rows pointing at one folder, and removing either one deletes the folder out from
    /// under the other. The two <em>other</em> creation paths do guard against it
    /// (<see cref="SkillFiles.CreateCustom"/> and <see cref="SkillFiles.ImportFromFolder"/> both
    /// refuse an existing directory), which is what makes this an inconsistency rather than a
    /// deliberate policy. Adding the guard here would change what the app does, so it stays.
    /// </remarks>
    public static WorkspaceSkill Add(
        SqliteConnection connection,
        string workspaceId,
        string skillName,
        string sourceRepo)
    {
        var row = new WorkspaceSkill(
            Guid.NewGuid().ToString(),
            workspaceId,
            skillName,
            sourceRepo,
            true,
            Clock.Now());

        Sql.Execute(connection,
            """
            INSERT INTO workspace_skills (id, workspace_id, skill_name, source_repo, enabled, installed_at)
            VALUES ($id, $workspaceId, $skillName, $sourceRepo, 1, $installedAt)
            """,
            ("$id", row.Id),
            ("$workspaceId", row.WorkspaceId),
            ("$skillName", row.SkillName),
            ("$sourceRepo", row.SourceRepo),
            ("$installedAt", row.InstalledAt));

        return row;
    }

    public static WorkspaceSkill? Get(SqliteConnection connection, string id) =>
        Sql.QuerySingle(connection, $"SELECT {Columns} FROM workspace_skills WHERE id = $id", Read, ("$id", id));

    public static void SetEnabled(SqliteConnection connection, string id, bool enabled) =>
        Sql.Execute(connection,
            "UPDATE workspace_skills SET enabled = $enabled WHERE id = $id",
            ("$id", id),
            ("$enabled", enabled ? 1 : 0));

    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM workspace_skills WHERE id = $id", ("$id", id));

    private static WorkspaceSkill Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        reader.GetString(5));
}
