using System.Text.Json;
using CodeFlow.Ai;
using CodeFlow.Git;
using CodeFlow.Ipc;
using CodeFlow.Review;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// The review-memory commands behind Settings → review memories, plus the registration set.
/// See <c>docs/business-rules/09-workspace-scoped.md</c> and <c>01-ipc-surface.md</c>.
/// </summary>
public sealed class ReviewCommandsTests
{
    /// <summary>
    /// The exact set this group registers.
    /// </summary>
    /// <remarks>
    /// The two review entry points, the two posting commands, and the seven the implementation
    /// memory commands — the whole pipeline.
    /// </remarks>
    private static readonly string[] Expected =
    [
        "review_pull_request", "review_pr_from_link",
        "post_pr_review_comment", "post_pr_link_review_comment",
        "list_review_runs", "get_review_run", "mark_review_finding",
        "delete_review_run", "delete_review_runs_for_pr", "purge_workspace_review_runs",
        "export_review_runs",
    ];

    [Fact]
    public void The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        using var db = new TempDatabase();
        var registry = Registry(db);

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Marking_a_finding_a_false_positive_stores_the_state_and_the_reason()
    {
        using var db = new TempDatabase();
        var run = Seed(db, Finding("F-001"));

        await Invoke(db, "mark_review_finding", new
        {
            runId = run,
            findingId = "F-001",
            estado = "falso_positivo",
            motivo = "el token nunca sale del proceso",
        });

        var stored = Assert.Single(Findings(db, run));
        Assert.Equal(MemoryFinding.FalsePositive, stored.Estado);
        Assert.Equal("el token nunca sale del proceso", stored.MotivoDescarte);
    }

    [Fact]
    public async Task A_blank_reason_is_dropped_rather_than_stored()
    {
        using var db = new TempDatabase();
        var run = Seed(db, Finding("F-001"));

        await Invoke(db, "mark_review_finding", new
        {
            runId = run,
            findingId = "F-001",
            estado = "ignorado",
            motivo = "   ",
        });

        // Otherwise the traceability section would render a dangling colon.
        Assert.Null(Assert.Single(Findings(db, run)).MotivoDescarte);
    }

    [Fact]
    public async Task Un_marking_a_finding_that_was_never_posted_returns_it_to_open()
    {
        using var db = new TempDatabase();
        var run = Seed(db, Finding("F-001") with
        {
            Estado = MemoryFinding.Ignored,
            MotivoDescarte = "por ahora no",
        });

        await Invoke(db, "mark_review_finding", new
        {
            runId = run,
            findingId = "F-001",
            estado = "abierto",
            motivo = (string?)null,
        });

        var stored = Assert.Single(Findings(db, run));
        Assert.Equal(MemoryFinding.Open, stored.Estado);
        Assert.Null(stored.MotivoDescarte);
    }

    [Fact]
    public async Task Un_marking_a_finding_that_has_a_thread_returns_it_to_posted()
    {
        using var db = new TempDatabase();
        var run = Seed(db, Finding("F-001") with { Estado = MemoryFinding.FalsePositive, ThreadId = 11 });

        await Invoke(db, "mark_review_finding", new
        {
            runId = run,
            findingId = "F-001",
            estado = "abierto",
            motivo = (string?)null,
        });

        // Not "abierto": the finding still owns a thread, and a later post must reply on it rather
        // than open a second one.
        Assert.Equal(MemoryFinding.Posted, Assert.Single(Findings(db, run)).Estado);
    }

    [Fact]
    public async Task Marking_a_finding_nobody_stored_says_so()
    {
        using var db = new TempDatabase();
        var run = Seed(db, Finding("F-001"));

        var failure = await Assert.ThrowsAsync<ReviewException>(() => Invoke(db, "mark_review_finding", new
        {
            runId = run,
            findingId = "F-404",
            estado = "ignorado",
            motivo = (string?)null,
        }).AsTask());

        Assert.Equal("Finding not found in this run", failure.Message);
    }

    [Fact]
    public async Task Marking_inside_a_run_nobody_stored_says_so()
    {
        using var db = new TempDatabase();

        var failure = await Assert.ThrowsAsync<ReviewException>(() => Invoke(db, "mark_review_finding", new
        {
            runId = "no-such-run",
            findingId = "F-001",
            estado = "ignorado",
            motivo = (string?)null,
        }).AsTask());

        Assert.Equal("Review run not found", failure.Message);
    }

    [Fact]
    public async Task Getting_a_run_nobody_stored_answers_null_rather_than_failing()
    {
        using var db = new TempDatabase();

        var reply = await Invoke(db, "get_review_run", new { id = "no-such-run" });

        Assert.Equal("null", reply);
    }

    [Fact]
    public async Task A_run_exports_as_a_folder_of_four_files()
    {
        using var db = new TempDatabase();
        var run = Seed(db, Finding("F-001"));
        var destination = Directory.CreateTempSubdirectory("codeflow-export-").FullName;

        try
        {
            var written = await Invoke(db, "export_review_runs", new
            {
                workspaceId = "ignored-when-an-id-is-given",
                id = run,
                destDir = destination,
            });

            Assert.Equal("1", written);

            var folder = Assert.Single(Directory.GetDirectories(destination));
            // The timestamp's colons and dots are replaced, because a colon is not a legal path
            // character on Windows.
            Assert.StartsWith("PR-42_", Path.GetFileName(folder), StringComparison.Ordinal);
            Assert.DoesNotContain(':', Path.GetFileName(folder));

            var ct = TestContext.Current.CancellationToken;
            Assert.Equal("cuerpo", await File.ReadAllTextAsync(Path.Combine(folder, "review.md"), ct));
            Assert.Equal("diff", await File.ReadAllTextAsync(Path.Combine(folder, "diff.patch"), ct));
            Assert.Contains("head_sha", await File.ReadAllTextAsync(Path.Combine(folder, "meta.json"), ct), StringComparison.Ordinal);
            Assert.Contains("F-001", await File.ReadAllTextAsync(Path.Combine(folder, "findings.json"), ct), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Exporting_a_run_nobody_stored_writes_nothing_rather_than_failing()
    {
        using var db = new TempDatabase();
        var destination = Directory.CreateTempSubdirectory("codeflow-export-").FullName;

        try
        {
            var written = await Invoke(db, "export_review_runs", new
            {
                workspaceId = "ws",
                id = "no-such-run",
                destDir = destination,
            });

            Assert.Equal("0", written);
            Assert.Empty(Directory.GetDirectories(destination));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    /// <summary>Writes one run holding the given findings, and answers its id.</summary>
    private static string Seed(TempDatabase db, params MemoryFinding[] findings)
    {
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));
        var project = db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id)));

        db.Do(c => ReviewRunStore.Add(
            c, "run-1", project.Id, workspace.Id, prId: 42, iter: 1, level: "completo",
            meta: """{"pr_title":"Arreglar el login","head_sha":"abc1234"}""",
            reviewMarkdown: "cuerpo",
            diff: "diff",
            findings: JsonSerializer.Serialize(findings.ToList(), ReviewJsonContext.Default.ListMemoryFinding)));

        return "run-1";
    }

    private static List<MemoryFinding> Findings(TempDatabase db, string runId)
    {
        var run = db.Use(c => ReviewRunStore.Get(c, runId));
        Assert.NotNull(run);
        return JsonSerializer.Deserialize(run.Findings, ReviewJsonContext.Default.ListMemoryFinding)!;
    }

    /// <summary>Dispatches a command the way the transport does, and answers its JSON reply.</summary>
    private static async ValueTask<string> Invoke(TempDatabase db, string command, object parameters)
    {
        var registry = Registry(db);
        Assert.True(registry.TryGet(command, out var handler));

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var reply = await handler(arguments.RootElement, CancellationToken.None);
        return System.Text.Encoding.UTF8.GetString(reply.Span);
    }

    /// <summary>
    /// The command surface, wired to collaborators no test in this file reaches.
    /// </summary>
    /// <remarks>
    /// The memory commands are pure database work; only the two review commands touch the HTTP
    /// client, the run registry and git, and those are exercised elsewhere. Handing them real but
    /// inert instances keeps this file about the memory half.
    /// </remarks>
    private static CommandRegistry Registry(TempDatabase db) =>
        new CommandRegistry().AddReviewCommands(
            db.Handle,
            new AiRunRegistry((_, _, _) => ValueTask.CompletedTask),
            new HttpClient(),
            new GitNetwork((_, _, _) => ValueTask.CompletedTask));

    private static MemoryFinding Finding(string id) => new()
    {
        Id = id,
        Severity = "warning",
        Tipo = "Bug",
        Categoria = "Seguridad",
        Subtitulo = "Algo",
        Archivo = "src/auth.ts",
        IntroducidoEnIter = 1,
    };
}
