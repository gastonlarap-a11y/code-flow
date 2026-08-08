using CodeFlow.Activity;
using CodeFlow.Storage;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Activity;

/// <summary>
/// Finished PR reviews and pre-commit analyses.
/// See <c>docs/business-rules/03-storage.md</c>.
/// </summary>
public sealed class JobHistoryStoreTests
{
    [Fact]
    public void Runs_come_back_newest_first()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        Add(db, project, "job-1", "done");
        Add(db, project, "job-2", "error");
        Add(db, project, "job-3", "done");

        Assert.Equal(["job-3", "job-2", "job-1"], db.Use(c => JobHistoryStore.List(c, project)).Select(j => j.Id));
    }

    [Fact]
    public void A_run_reads_back_exactly_as_it_was_returned()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        var written = db.Use(c => JobHistoryStore.Add(
            c, "job-1", project, "analyze-changes", "Análisis de cambios", "error", null, "the CLI exploded", "{}"));

        Assert.Equal(written, Assert.Single(db.Use(c => JobHistoryStore.List(c, project))));
        Assert.Null(written.Result);
        Assert.Null(written.CustomLabel);
    }

    [Fact]
    public void Renaming_writes_the_custom_label_and_leaves_the_generated_one_alone()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        Add(db, project, "job-1", "done");
        db.Do(c => JobHistoryStore.Rename(c, "job-1", "Before the release"));

        var entry = Assert.Single(db.Use(c => JobHistoryStore.List(c, project)));

        Assert.Equal("Before the release", entry.CustomLabel);
        Assert.Equal("Análisis de cambios", entry.Label);
    }

    [Fact]
    public void Deleting_a_run_that_is_still_in_flight_is_not_an_error()
    {
        // Best-effort by design: a running job has no row yet, and the frontend removes it from
        // memory regardless of what this answers.
        using var db = new TempDatabase();

        db.Do(c => JobHistoryStore.Delete(c, "never-recorded"));
    }

    [Fact]
    public void Deleting_the_project_cascades_to_its_job_history()
    {
        using var db = new TempDatabase();
        var project = Project(db);

        Add(db, project, "job-1", "done");
        db.Do(c => ProjectStore.Delete(c, project));

        Assert.Empty(db.Use(c => JobHistoryStore.List(c, project)));
    }

    private static string Project(TempDatabase db)
    {
        var workspace = db.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#6366f1"));
        return db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id))).Id;
    }

    /// <summary>Records one run, guaranteeing it sorts after the previous one — see the note in <see cref="ActivityLogStoreTests"/>.</summary>
    private static void Add(TempDatabase db, string projectId, string id, string status)
    {
        db.Use(c => JobHistoryStore.Add(
            c, id, projectId, "analyze-changes", "Análisis de cambios", status,
            status == "done" ? "the analysis" : null,
            status == "done" ? null : "the failure",
            "{}"));

        Thread.Sleep(2);
    }
}
