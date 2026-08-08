using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// Project CRUD and the reparenting rules.
/// See <c>docs/business-rules/09-workspace-scoped.md</c> <c>WS-002</c>, <c>WS-003</c>.
/// </summary>
public sealed class ProjectStoreTests
{
    [Fact]
    public void A_created_project_reads_back_exactly_as_it_was_returned()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        var input = WorkspaceStoreTests.NewProjectIn(workspace.Id) with
        {
            RemoteUrl = "https://example.invalid/repo.git",
            GithubOwner = "owner",
            GithubRepo = "repo",
        };

        var created = db.Use(c => ProjectStore.Create(c, input));

        // The six link columns are nullable and independent; a round trip must not turn an unset
        // one into an empty string or lose a set one.
        Assert.Equal(created, db.Use(c => ProjectStore.Get(c, created.Id)));
        Assert.Null(created.AdoOrg);
        Assert.Equal("owner", created.GithubOwner);
    }

    [Fact]
    public void Getting_an_unknown_project_is_null_rather_than_an_error()
    {
        using var db = new TempDatabase();

        Assert.Null(db.Use(c => ProjectStore.Get(c, "nope")));
    }

    [Fact]
    public void Moving_a_project_takes_its_review_history_along()
    {
        using var db = new TempDatabase();

        var origin = db.Use(c => WorkspaceStore.Create(c, "Origin", "folder", "#111111"));
        var destination = db.Use(c => WorkspaceStore.Create(c, "Destination", "folder", "#222222"));
        var project = db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(origin.Id)));

        db.Do(c => InsertReviewRun(c, project.Id, origin.Id));
        db.Do(c => ProjectStore.MoveToWorkspace(c, project.Id, destination.Id));

        Assert.Equal(destination.Id, db.Use(c => ProjectStore.Get(c, project.Id))!.WorkspaceId);
        Assert.Empty(db.Use(c => ProjectStore.List(c, origin.Id)));
        Assert.Single(db.Use(c => ProjectStore.List(c, destination.Id)));

        // BUG-STORE-b, closed: the denormalised workspace_id moves inside the same transaction,
        // so the history follows the project — visible in the destination's list, and the old
        // workspace's purge can no longer delete what it does not own.
        Assert.Equal(destination.Id, db.Use(c => ReviewRunWorkspace(c, project.Id)));
        Assert.Single(db.Use(c => CodeFlow.Review.ReviewRunStore.List(c, destination.Id)));
        Assert.Empty(db.Use(c => CodeFlow.Review.ReviewRunStore.List(c, origin.Id)));

        db.Do(c => CodeFlow.Review.ReviewRunStore.Purge(c, origin.Id));
        Assert.Single(db.Use(c => CodeFlow.Review.ReviewRunStore.List(c, destination.Id)));
    }

    [Fact]
    public void Moving_a_project_to_a_workspace_that_does_not_exist_fails()
    {
        using var db = new TempDatabase();

        var workspace = db.Use(c => WorkspaceStore.Create(c, "Origin", "folder", "#111111"));
        var project = db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id)));

        // Resolves AMBIGUOUS-WS-a: 1.7.2 could not be read off the implementation alone because the
        // answer depends on the pragma, which is set elsewhere. Database.Open sets foreign_keys on
        // the one long-lived connection, exactly as 1.7.2's migration runner does, so the
        // statement is rejected rather than orphaning the project.
        var failure = Assert.Throws<SqliteException>(
            () => db.Do(c => ProjectStore.MoveToWorkspace(c, project.Id, "nope")));
        Assert.Contains("FOREIGN KEY", failure.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(workspace.Id, db.Use(c => ProjectStore.Get(c, project.Id))!.WorkspaceId);
    }

    [Fact]
    public void Deleting_a_project_cascades_to_its_own_rows()
    {
        using var db = new TempDatabase();

        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));
        var project = db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id)));
        db.Do(c => InsertReviewRun(c, project.Id, workspace.Id));

        db.Do(c => ProjectStore.Delete(c, project.Id));

        Assert.Null(db.Use(c => ProjectStore.Get(c, project.Id)));
        Assert.Null(db.Use(c => ReviewRunWorkspace(c, project.Id)));
    }

    private static void InsertReviewRun(SqliteConnection connection, string projectId, string workspaceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO review_runs (id, project_id, workspace_id, pr_id, iter, level, review_md, created_at)
            VALUES ($id, $projectId, $workspaceId, 1, 1, 'standard', '', '2026-01-01T00:00:00.0000000+00:00')
            """;
        command.Parameters.AddWithValue("$id", $"run-{projectId}");
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.ExecuteNonQuery();
    }

    private static string? ReviewRunWorkspace(SqliteConnection connection, string projectId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT workspace_id FROM review_runs WHERE project_id = $projectId";
        command.Parameters.AddWithValue("$projectId", projectId);
        return command.ExecuteScalar() as string;
    }
}
