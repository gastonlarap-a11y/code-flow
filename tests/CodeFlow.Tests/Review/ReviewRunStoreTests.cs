using CodeFlow.Review;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// The <c>review_runs</c> table.
/// See <c>docs/business-rules/03-storage.md</c> <c>STORE-010</c>, <c>STORE-013</c>, <c>BUG-STORE-b</c>.
/// </summary>
public sealed class ReviewRunStoreTests
{
    [Fact]
    public void A_run_is_idempotent_by_id()
    {
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);

        db.Do(c => Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1, reviewMarkdown: "primera"));
        // A retried job reusing its own id must not produce a second row.
        db.Do(c => Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 2, reviewMarkdown: "segunda"));

        Assert.Equal(1L, db.Use(c => ReviewRunStore.Count(c, projectId, 42)));
        var stored = db.Use(c => ReviewRunStore.Get(c, "run-1"));
        Assert.NotNull(stored);
        Assert.Equal("primera", stored.ReviewMd);
        Assert.Equal(1, stored.Iter);
    }

    [Fact]
    public void Only_findings_can_be_changed_once_a_run_is_written()
    {
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);

        db.Do(c => Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1, reviewMarkdown: "cuerpo"));
        db.Do(c => ReviewRunStore.SetFindings(c, "run-1", """[{"id":"F-001"}]"""));

        var stored = db.Use(c => ReviewRunStore.Get(c, "run-1"));
        Assert.NotNull(stored);
        Assert.Equal("""[{"id":"F-001"}]""", stored.Findings);
        Assert.Equal("cuerpo", stored.ReviewMd);
    }

    [Fact]
    public void The_latest_head_comes_from_the_newest_run_by_creation_time()
    {
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);

        db.Do(c =>
        {
            Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1, meta: """{"head_sha":"aaa"}""");
            Add(c, "run-2", projectId, workspaceId, prId: 42, iter: 2, meta: """{"head_sha":"bbb"}""");
            // Newest by created_at, not by iteration or insertion order.
            Backdate(c, "run-2", "2000-01-01T00:00:00Z");
        });

        Assert.Equal("aaa", db.Use(c => ReviewRunStore.LatestHead(c, projectId, 42)));
    }

    [Fact]
    public void A_run_with_no_recorded_head_answers_nothing()
    {
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);

        // An empty SHA must not short-circuit a re-review the way a real one does.
        db.Do(c => Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1, meta: """{"head_sha":""}"""));

        Assert.Null(db.Use(c => ReviewRunStore.LatestHead(c, projectId, 42)));
    }

    [Fact]
    public void A_pr_with_no_runs_answers_nothing()
    {
        using var db = new TempDatabase();
        var (_, projectId) = Project(db);

        Assert.Null(db.Use(c => ReviewRunStore.LatestHead(c, projectId, 42)));
        Assert.Null(db.Use(c => ReviewRunStore.LatestFindings(c, projectId, 42)));
        Assert.Equal(0L, db.Use(c => ReviewRunStore.Count(c, projectId, 42)));
    }

    [Fact]
    public void The_listing_joins_the_project_name_and_reads_the_title_out_of_the_runs_own_meta()
    {
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);

        db.Do(c => Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1,
            meta: """{"pr_title":"Arreglar el login"}""",
            findings: """[{"id":"F-001"},{"id":"F-002"}]"""));

        var listed = Assert.Single(db.Use(c => ReviewRunStore.List(c, workspaceId)));
        Assert.Equal("Repo", listed.ProjectName);
        Assert.Equal("Arreglar el login", listed.PrTitle);
        Assert.Equal(2L, listed.FindingsCount);
    }

    [Fact]
    public void A_run_whose_meta_carries_no_title_lists_with_an_empty_one()
    {
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);

        db.Do(c => Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1, meta: "{}", findings: "[]"));

        var listed = Assert.Single(db.Use(c => ReviewRunStore.List(c, workspaceId)));
        Assert.Equal("", listed.PrTitle);
        Assert.Equal(0L, listed.FindingsCount);
    }

    [Fact]
    public void Deleting_a_project_takes_its_runs_with_it()
    {
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);

        db.Do(c => Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1));
        db.Do(c => ProjectStore.Delete(c, projectId));

        Assert.Empty(db.Use(c => ReviewRunStore.List(c, workspaceId)));
        Assert.Null(db.Use(c => ReviewRunStore.Get(c, "run-1")));
    }

    [Fact]
    public void Deleting_one_prs_history_leaves_the_others_alone()
    {
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);

        db.Do(c =>
        {
            Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1);
            Add(c, "run-2", projectId, workspaceId, prId: 42, iter: 2);
            Add(c, "run-3", projectId, workspaceId, prId: 43, iter: 1);
        });

        db.Do(c => ReviewRunStore.DeleteForPr(c, projectId, 42));

        Assert.Equal(0L, db.Use(c => ReviewRunStore.Count(c, projectId, 42)));
        Assert.Equal(1L, db.Use(c => ReviewRunStore.Count(c, projectId, 43)));
    }

    [Fact]
    public void Purging_a_workspace_wipes_only_that_workspaces_runs()
    {
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);
        var other = db.Use(c => WorkspaceStore.Create(c, "Other", "folder", "#222222"));

        db.Do(c =>
        {
            Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1);
            Add(c, "run-2", projectId, other.Id, prId: 42, iter: 2);
        });

        db.Do(c => ReviewRunStore.Purge(c, workspaceId));

        Assert.Empty(db.Use(c => ReviewRunStore.List(c, workspaceId)));
        Assert.Single(db.Use(c => ReviewRunStore.List(c, other.Id)));
    }

    [Fact]
    public void Moving_a_project_moves_its_review_history_with_it()
    {
        // BUG-STORE-b, closed: MoveToWorkspace updates the denormalised workspace_id in the same
        // transaction, so the history lands where the user now looks for it.
        using var db = new TempDatabase();
        var (workspaceId, projectId) = Project(db);
        var destination = db.Use(c => WorkspaceStore.Create(c, "Destination", "folder", "#222222"));

        db.Do(c => Add(c, "run-1", projectId, workspaceId, prId: 42, iter: 1));
        db.Do(c => ProjectStore.MoveToWorkspace(c, projectId, destination.Id));

        Assert.Single(db.Use(c => ReviewRunStore.List(c, destination.Id)));
        Assert.Empty(db.Use(c => ReviewRunStore.List(c, workspaceId)));
    }

    private static (string WorkspaceId, string ProjectId) Project(TempDatabase db)
    {
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));
        var project = db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id)));
        return (workspace.Id, project.Id);
    }

    private static void Add(
        SqliteConnection connection,
        string id,
        string projectId,
        string workspaceId,
        long prId,
        int iter,
        string meta = "{}",
        string reviewMarkdown = "cuerpo",
        string findings = "[]") =>
        ReviewRunStore.Add(
            connection, id, projectId, workspaceId, prId, iter, "completo", meta, reviewMarkdown, "diff", findings);

    /// <summary>Forces a run's <c>created_at</c>, so "newest" can be asserted rather than raced.</summary>
    private static void Backdate(SqliteConnection connection, string id, string createdAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE review_runs SET created_at = $createdAt WHERE id = $id";
        command.Parameters.AddWithValue("$createdAt", createdAt);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
