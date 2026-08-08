using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// The three workspace-scoped rosters that share an upsert shape: review contexts, SDD agents and
/// MCP servers. See <c>docs/business-rules/09-workspace-scoped.md</c>.
/// </summary>
/// <remarks>
/// They are tested together because the interesting behaviour is the same in all three — what a
/// null id does, and what an edit must not overwrite — and because the differences between them
/// (agents preserve <c>sort_order</c> too; contexts and MCP servers have no such column) are only
/// visible side by side.
/// </remarks>
public sealed class UpsertTests
{
    [Fact]
    public void A_null_id_mints_a_new_row_each_time()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        var first = db.Use(c => ReviewContextStore.Upsert(c, id: null, workspace.Id, "One", "body", enabled: true));
        var second = db.Use(c => ReviewContextStore.Upsert(c, id: null, workspace.Id, "Two", "body", enabled: true));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, db.Use(c => ReviewContextStore.List(c, workspace.Id)).Count);
    }

    [Fact]
    public void Editing_a_review_context_keeps_the_stored_creation_time_but_returns_a_fresh_one()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        var created = db.Use(c => ReviewContextStore.Upsert(c, id: null, workspace.Id, "One", "body", enabled: true));

        // Backdated so the two timestamps cannot coincide: Clock.Now has sub-microsecond
        // resolution, and two calls a few statements apart could otherwise render identically.
        const string Backdated = "2020-01-01T00:00:00.0000000+00:00";
        db.Do(c => Backdate(c, created.Id, Backdated));

        var edited = db.Use(c =>
            ReviewContextStore.Upsert(c, created.Id, workspace.Id, "Renamed", "new body", enabled: false));

        Assert.Equal(created.Id, edited.Id);
        Assert.Equal("Renamed", edited.Name);
        Assert.False(edited.Enabled);

        var stored = Assert.Single(db.Use(c => ReviewContextStore.List(c, workspace.Id)));
        Assert.Equal(Backdated, stored.CreatedAt);
        Assert.Equal("Renamed", stored.Name);

        // The returned record and the stored row disagree on created_at after an edit, because the
        // reference stamps the struct before running the statement and the conflict clause never
        // touches the column. Reproduced, not corrected — the frontend
        // splices the returned value into its local state, so changing it changes what the settings
        // screen shows between an edit and the next refresh.
        Assert.NotEqual(stored.CreatedAt, edited.CreatedAt);
    }

    [Fact]
    public void Review_contexts_are_listed_in_insertion_order()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        var first = db.Use(c => ReviewContextStore.Upsert(c, id: null, workspace.Id, "One", "a", enabled: true));
        var second = db.Use(c => ReviewContextStore.Upsert(c, id: null, workspace.Id, "Two", "b", enabled: true));

        // Backdated rather than relying on two Clock.Now calls rendering differently: the order is
        // what is under test, and a clock-resolution tie would make this pass or fail at random.
        db.Do(c => Backdate(c, second.Id, "2020-01-01T00:00:00.0000000+00:00"));

        // No sort_order column on this table, unlike agents — the user cannot rank contexts, and
        // the list is ordered purely by the stored timestamp, compared as text.
        Assert.Equal([second.Id, first.Id], db.Use(c => ReviewContextStore.List(c, workspace.Id)).Select(x => x.Id));
    }

    [Fact]
    public void Editing_an_agent_keeps_its_sort_order_and_creation_time()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        var created = db.Use(c =>
            WorkspaceAgentStore.Upsert(c, id: null, workspace.Id, "Reviewer", "role", "claude", "m", "p", enabled: true));
        var edited = db.Use(c =>
            WorkspaceAgentStore.Upsert(c, created.Id, workspace.Id, "Auditor", "role2", "codex", "m2", "p2", enabled: false));

        Assert.Equal(created.CreatedAt, edited.CreatedAt);
        Assert.Equal(created.SortOrder, edited.SortOrder);
        Assert.Equal("codex", edited.Provider);
        Assert.Equal(edited, Assert.Single(db.Use(c => WorkspaceAgentStore.List(c, workspace.Id))));
    }

    [Fact]
    public void A_caller_supplied_id_that_matches_no_row_inserts_it_with_sort_order_zero()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        var created = db.Use(c =>
            WorkspaceAgentStore.Upsert(c, "chosen-id", workspace.Id, "Reviewer", "role", "claude", "m", "p", true));

        Assert.Equal("chosen-id", created.Id);
        Assert.Equal(0L, created.SortOrder);
    }

    [Fact]
    public void The_agent_roster_starts_empty()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        // Deliberately unseeded, unlike the two prompt kinds — the user creates their own.
        Assert.Empty(db.Use(c => WorkspaceAgentStore.List(c, workspace.Id)));
    }

    [Fact]
    public void An_mcp_servers_args_and_env_survive_a_round_trip_verbatim()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        const string args = "  server.js   --port 3000 ";
        const string env = "TOKEN=abc\nDEBUG=1";

        var created = db.Use(c => WorkspaceMcpStore.Upsert(c, null, workspace.Id, "Local", "node", args, env, true));

        // Stored as typed: the dispatch layer re-splits them when it writes a run's mcp.json, and
        // normalising here would change what the settings screen gives back.
        var stored = Assert.Single(db.Use(c => WorkspaceMcpStore.List(c, workspace.Id)));
        Assert.Equal(args, stored.Args);
        Assert.Equal(env, stored.Env);
        Assert.Equal(created, stored);
    }

    [Fact]
    public void Deleting_removes_only_the_targeted_row()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        var kept = db.Use(c => ReviewContextStore.Upsert(c, null, workspace.Id, "Kept", "a", enabled: true));
        var removed = db.Use(c => ReviewContextStore.Upsert(c, null, workspace.Id, "Removed", "b", enabled: true));

        db.Do(c => ReviewContextStore.Delete(c, removed.Id));

        Assert.Equal(kept.Id, Assert.Single(db.Use(c => ReviewContextStore.List(c, workspace.Id))).Id);
    }

    private static void Backdate(SqliteConnection connection, string id, string createdAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE review_contexts SET created_at = $createdAt WHERE id = $id";
        command.Parameters.AddWithValue("$createdAt", createdAt);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
