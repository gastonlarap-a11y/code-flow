using CodeFlow.Ai;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// Workspace creation, ordering and the delete cascade.
/// See <c>docs/business-rules/09-workspace-scoped.md</c>.
/// </summary>
public sealed class WorkspaceStoreTests
{
    [Fact]
    public void Creating_a_workspace_seeds_both_editable_prompts()
    {
        using var db = new TempDatabase();

        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        // The real built-in text, not blanks: the editor opens on the methodology the user is
        // about to change, and a blank save is what resets it.
        Assert.Equal(
            Prompts.DefaultPrReviewStandard,
            db.Use(c => Settings.GetWorkspacePrompt(c, workspace.Id, "review_standard")));
        Assert.Equal(
            Prompts.DefaultPrDescriptionTemplate,
            db.Use(c => Settings.GetWorkspacePrompt(c, workspace.Id, "pr_description")));

        // sdd_stages is never seeded — it only gets a row when the user saves one.
        Assert.Equal(0L, db.Use(c => Count(c, "workspace_prompts", "kind = 'sdd_stages'")));
    }

    [Fact]
    public void Workspaces_are_listed_by_sort_order_then_creation_time()
    {
        using var db = new TempDatabase();

        var first = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#111111"));
        var second = db.Use(c => WorkspaceStore.Create(c, "Second", "folder", "#222222"));

        // Nothing writes a non-zero sort_order, so in practice this is creation order compared as
        // text — which is why the timestamp format is reproduced rather than modernised. Both parts
        // of the key are exercised: backdating flips the tie-break, a sort_order flips it back.
        db.Do(c => Backdate(c, second.Id, "2020-01-01T00:00:00.0000000+00:00"));
        Assert.Equal([second.Id, first.Id], db.Use(WorkspaceStore.List).Select(w => w.Id));

        db.Do(c => SetSortOrder(c, second.Id, 1));
        Assert.Equal([first.Id, second.Id], db.Use(WorkspaceStore.List).Select(w => w.Id));
    }

    [Fact]
    public void Deleting_a_workspace_cascades_to_everything_scoped_to_it()
    {
        using var db = new TempDatabase();

        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));
        db.Do(c =>
        {
            ProjectStore.Create(c, NewProjectIn(workspace.Id));
            ReviewContextStore.Upsert(c, id: null, workspace.Id, "Context", "body", enabled: true);
            WorkspaceAgentStore.Upsert(c, id: null, workspace.Id, "Agent", "role", "claude", "m", "p", enabled: true);
            WorkspaceMcpStore.Upsert(c, id: null, workspace.Id, "Mcp", "node", "server.js", "K=v", enabled: true);
        });

        db.Do(c => WorkspaceStore.Delete(c, workspace.Id));

        // One DELETE; SQLite does the rest. The pragma is per-connection, so a missing one would
        // not fail here — it would silently orphan every row below.
        foreach (var table in new[] { "projects", "review_contexts", "workspace_agents", "workspace_mcps", "workspace_prompts" })
        {
            Assert.Equal(0L, db.Use(c => Count(c, table, "1 = 1")));
        }
    }

    [Fact]
    public void Updating_a_workspace_colour_leaves_its_other_columns_alone()
    {
        using var db = new TempDatabase();

        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#111111"));
        db.Do(c => WorkspaceStore.UpdateColor(c, workspace.Id, "#222222"));

        var stored = Assert.Single(db.Use(WorkspaceStore.List));
        Assert.Equal("#222222", stored.Color);
        Assert.Equal(workspace with { Color = "#222222" }, stored);
    }

    [Fact]
    public void Renaming_a_workspace_leaves_its_other_columns_alone()
    {
        using var db = new TempDatabase();

        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#111111"));
        db.Do(c => WorkspaceStore.Rename(c, workspace.Id, "Renamed"));

        var stored = Assert.Single(db.Use(WorkspaceStore.List));
        Assert.Equal(workspace with { Name = "Renamed" }, stored);
    }

    [Fact]
    public void Renaming_trims_the_name()
    {
        using var db = new TempDatabase();

        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#111111"));
        db.Do(c => WorkspaceStore.Rename(c, workspace.Id, "  Spaced  "));

        Assert.Equal("Spaced", Assert.Single(db.Use(WorkspaceStore.List)).Name);
    }

    /// A workspace is picked from a list by its name alone, so a blank one is a row nobody can
    /// tell apart. Refused at the store, not only in the form that happens to call it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_rename_is_refused_and_the_old_name_survives(string blank)
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#111111"));

        Assert.Throws<ArgumentException>(() => db.Do(c => WorkspaceStore.Rename(c, workspace.Id, blank)));
        Assert.Equal("First", Assert.Single(db.Use(WorkspaceStore.List)).Name);
    }

    [Fact]
    public void A_git_identity_override_round_trips_and_clears_as_a_pair()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "Work", "folder", "#111111"));

        db.Do(c => WorkspaceStore.UpdateGitIdentity(c, workspace.Id, "Work Person", "work@company.com"));
        var stored = Assert.Single(db.Use(WorkspaceStore.List));
        Assert.Equal("Work Person", stored.GitName);
        Assert.Equal("work@company.com", stored.GitEmail);

        // Both nulls clear the override; the row survives with everything else intact.
        db.Do(c => WorkspaceStore.UpdateGitIdentity(c, workspace.Id, null, null));
        stored = Assert.Single(db.Use(WorkspaceStore.List));
        Assert.Null(stored.GitName);
        Assert.Null(stored.GitEmail);
        Assert.Equal(workspace, stored);
    }

    [Fact]
    public void Resolving_an_identity_finds_the_workspace_through_the_project_path()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "Work", "folder", "#111111"));
        db.Do(c =>
        {
            ProjectStore.Create(c, NewProjectIn(workspace.Id));
            WorkspaceStore.UpdateGitIdentity(c, workspace.Id, "Work Person", "work@company.com");
        });

        var (name, email) = db.Use(c => WorkspaceStore.ResolveGitIdentity(c, "/tmp/repo"));

        Assert.Equal("Work Person", name);
        Assert.Equal("work@company.com", email);
    }

    [Fact]
    public void Resolving_without_an_override_or_a_registered_project_yields_nulls()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "Home", "folder", "#111111"));
        db.Do(c => ProjectStore.Create(c, NewProjectIn(workspace.Id)));

        // A registered project whose workspace has no override…
        var (name, email) = db.Use(c => WorkspaceStore.ResolveGitIdentity(c, "/tmp/repo"));
        Assert.Null(name);
        Assert.Null(email);

        // …and a path no project is registered at — both read as "use the global identity".
        var (unregisteredName, unregisteredEmail) =
            db.Use(c => WorkspaceStore.ResolveGitIdentity(c, "/tmp/unregistered"));
        Assert.Null(unregisteredName);
        Assert.Null(unregisteredEmail);
    }

    [Fact]
    public void Two_projects_sharing_a_path_resolve_to_one_workspace_rather_than_failing()
    {
        // Nothing in the schema prevents this; WS-008 documents it as "first match", not a
        // guarantee of which. What matters is that it answers instead of throwing.
        using var db = new TempDatabase();
        var first = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#111111"));
        var second = db.Use(c => WorkspaceStore.Create(c, "Second", "folder", "#222222"));
        db.Do(c =>
        {
            ProjectStore.Create(c, NewProjectIn(first.Id));
            ProjectStore.Create(c, NewProjectIn(second.Id));
            WorkspaceStore.UpdateGitIdentity(c, first.Id, "First Person", "first@example.com");
            WorkspaceStore.UpdateGitIdentity(c, second.Id, "Second Person", "second@example.com");
        });

        var (name, _) = db.Use(c => WorkspaceStore.ResolveGitIdentity(c, "/tmp/repo"));

        Assert.True(name is "First Person" or "Second Person", $"unexpected identity '{name}'");
    }

    internal static NewProject NewProjectIn(string workspaceId) => new(
        workspaceId,
        Name: "Repo",
        LocalPath: "/tmp/repo",
        RemoteUrl: null,
        Color: "#6366f1",
        Icon: "git-branch",
        AdoOrg: null,
        AdoProject: null,
        AdoRepoId: null,
        GithubOwner: null,
        GithubRepo: null,
        GithubHost: null);

    private static void Backdate(SqliteConnection connection, string id, string createdAt) =>
        Update(connection, "UPDATE workspaces SET created_at = $value WHERE id = $id", id, createdAt);

    private static void SetSortOrder(SqliteConnection connection, string id, long sortOrder) =>
        Update(connection, "UPDATE workspaces SET sort_order = $value WHERE id = $id", id, sortOrder);

    private static void Update(SqliteConnection connection, string sql, string id, object value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection connection, string table, string where)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
        return (long)command.ExecuteScalar()!;
    }
}
