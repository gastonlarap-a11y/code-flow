using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Ai;
using CodeFlow.Git;
using CodeFlow.Ipc;
using CodeFlow.Providers;
using CodeFlow.Platform;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Review;

/// <summary>
/// The review-memory commands — everything the memory manager in Settings does with a saved run.
/// </summary>
/// <remarks>
/// <para>
/// These live here rather than with the other settings commands in
/// <c>Workspaces/WorkspaceCommands.cs</c>: they read the table this feature writes, and the rule
/// that keeps a feature findable is that its commands sit in its own folder.
/// </para>
/// <para>
/// Every handler is a plain database call gated by <see cref="Database"/>, so none of them needs an
/// explicit <see cref="Task.Run"/> — a contended gate suspends, and an uncontended query on an
/// indexed table is bounded and small. <c>export_review_runs</c> is the exception and says why.
/// </para>
/// </remarks>
public static class ReviewCommands
{
    public static CommandRegistry AddReviewCommands(
        this CommandRegistry registry,
        Database database,
        AiRunRegistry runs,
        HttpClient http,
        GitNetwork git) =>
        registry
            // ---------- the review itself ----------
            .Add("review_pull_request", async (p, ct) =>
            {
                var review = await ReviewRun.ForProjectAsync(
                    database, http, git, AiEngineRunner.Bind(runs, http),
                    Arg(p, "projectId"), Number(p, "prId"), Arg(p, "jobId"), Arg(p, "level"), Agent(p), ct)
                    .ConfigureAwait(false);

                return Json(review, ReviewJsonContext.Default.String);
            })
            .Add("review_pr_from_link", async (p, ct) =>
            {
                var review = await ReviewRun.ForLinkAsync(
                    database, http, AiEngineRunner.Bind(runs, http),
                    Arg(p, "url"), Arg(p, "jobId"), Arg(p, "level"), Arg(p, "workspaceId"), Agent(p),
                    AppPaths.PrLinkReviewsDirectory, ct)
                    .ConfigureAwait(false);

                return Json(review, ReviewJsonContext.Default.String);
            })
            // ---------- publishing what it found ----------
            .Add("post_pr_review_comment", async (p, ct) =>
            {
                await ReviewPosting.PublishAsync(
                    database, http, Arg(p, "projectId"), Number(p, "prId"), Arg(p, "runId"),
                    Items(p), Bool(p, "postSummary"), Optional(p, "summary"), ct).ConfigureAwait(false);

                return Unit;
            })
            .Add("post_pr_link_review_comment", async (p, ct) =>
            {
                await ReviewPosting.PublishFromLinkAsync(
                    database, http, Arg(p, "url"),
                    Items(p), Bool(p, "postSummary"), Optional(p, "summary"), ct).ConfigureAwait(false);

                return Unit;
            })
            // ---------- the memory it leaves behind ----------
            .Add("list_review_runs", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return Read(database, c => ReviewRunStore.List(c, workspaceId),
                    ReviewJsonContext.Default.ListReviewRunSummary, ct);
            })
            .Add("get_review_run", (p, ct) =>
            {
                var id = Arg(p, "id");
                // Explicit T: a missing run resolves to null, and the generated type info for the
                // same underlying type is annotated non-nullable.
                return Read<ReviewRunDetail?>(database, c => ReviewRunStore.Get(c, id),
                    ReviewJsonContext.Default.ReviewRunDetail!, ct);
            })
            .Add("mark_review_finding", (p, ct) =>
            {
                var runId = Arg(p, "runId");
                var findingId = Arg(p, "findingId");
                var estado = Arg(p, "estado");
                var motivo = Optional(p, "motivo");
                return WriteUnit(database, c => MarkFinding(c, runId, findingId, estado, motivo), ct);
            })
            .Add("delete_review_run", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => ReviewRunStore.Delete(c, id), ct);
            })
            .Add("delete_review_runs_for_pr", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var prId = Number(p, "prId");
                return WriteUnit(database, c => ReviewRunStore.DeleteForPr(c, projectId, prId), ct);
            })
            .Add("purge_workspace_review_runs", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return WriteUnit(database, c => ReviewRunStore.Purge(c, workspaceId), ct);
            })
            .Add("export_review_runs", async (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var id = Optional(p, "id");
                var destination = Arg(p, "destDir");

                // Collected under the gate, written outside it: a folder chosen on a network share
                // can block for seconds, and holding the one SQLite connection through that would
                // stall every other command.
                var runs = await database.ReadAsync(c => Load(c, workspaceId, id), ct).ConfigureAwait(false);
                return Json(Export(runs, destination), ReviewJsonContext.Default.Int32);
            });

    /// <summary>
    /// Flips one finding's state inside a saved run — false positive, ignored, or back to open.
    /// </summary>
    /// <remarks>
    /// Un-marking restores <c>posteado</c> rather than <c>abierto</c> when the finding still has a
    /// thread, so the posting flow keeps replying on it instead of opening a second one. The reason
    /// is dropped whenever it is blank, so an empty box never renders as a dangling colon in the
    /// traceability section.
    /// </remarks>
    private static void MarkFinding(
        SqliteConnection connection, string runId, string findingId, string estado, string? motivo)
    {
        var run = ReviewRunStore.Get(connection, runId)
            ?? throw new ReviewException("Review run not found");

        var findings = JsonSerializer.Deserialize(run.Findings, ReviewJsonContext.Default.ListMemoryFinding)
            ?? throw new ReviewException("Review run not found");

        var index = findings.FindIndex(f => f.Id == findingId);
        if (index < 0)
        {
            throw new ReviewException("Finding not found in this run");
        }

        findings[index] = estado switch
        {
            MemoryFinding.FalsePositive or MemoryFinding.Ignored => findings[index] with
            {
                Estado = estado,
                MotivoDescarte = string.IsNullOrWhiteSpace(motivo) ? null : motivo,
            },
            _ => findings[index] with
            {
                Estado = findings[index].ThreadId is null ? MemoryFinding.Open : MemoryFinding.Posted,
                MotivoDescarte = null,
            },
        };

        ReviewRunStore.SetFindings(
            connection, runId, JsonSerializer.Serialize(findings, ReviewJsonContext.Default.ListMemoryFinding));
    }

    /// <summary>The runs an export covers: one named run, or every run in the workspace.</summary>
    /// <remarks>
    /// A named run is exported whether or not it belongs to the workspace that was passed, and a
    /// name that matches nothing exports zero runs rather than failing. Both are deliberate: the
    /// export is a diagnostic, and failing it on a typo helps nobody.
    /// </remarks>
    private static List<ReviewRunDetail> Load(SqliteConnection connection, string workspaceId, string? id)
    {
        if (id is not null)
        {
            return ReviewRunStore.Get(connection, id) is { } run ? [run] : [];
        }

        return ReviewRunStore.List(connection, workspaceId)
            .Select(summary => ReviewRunStore.Get(connection, summary.Id))
            .OfType<ReviewRunDetail>()
            .ToList();
    }

    /// <summary>Writes each run as its own folder of four files, and reports how many were written.</summary>
    /// <remarks>
    /// The folder name carries the timestamp with its colons and dots replaced, because a colon is
    /// not a legal path character on Windows and a run exported there would otherwise fail.
    /// </remarks>
    private static int Export(List<ReviewRunDetail> runs, string destination)
    {
        var written = 0;
        foreach (var run in runs)
        {
            var stamp = run.CreatedAt.Replace(':', '-').Replace('.', '-');
            var directory = Path.Combine(
                destination, string.Create(CultureInfo.InvariantCulture, $"PR-{run.PrId}_{stamp}"));

            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "review.md"), run.ReviewMd);
            File.WriteAllText(Path.Combine(directory, "meta.json"), run.Meta);
            File.WriteAllText(Path.Combine(directory, "diff.patch"), run.Diff);
            File.WriteAllText(Path.Combine(directory, "findings.json"), run.Findings);
            written++;
        }

        return written;
    }

    // ---------- dispatch helpers ----------

    private static async ValueTask<ReadOnlyMemory<byte>> Read<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await database.ReadAsync(work, cancellationToken).ConfigureAwait(false);
        return Json(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> WriteUnit(
        Database database, Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return "null"u8.ToArray();
    }

    /// <summary>The reply for a command that answers nothing.</summary>
    private static ReadOnlyMemory<byte> Unit => "null"u8.ToArray();

    private static ReadOnlyMemory<byte> Json<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToUtf8Bytes(value, type);

    // ---------- argument helpers ----------
    //
    // Arguments arrive camelCase, returned shapes are snake_case — the same split every other
    // feature has.

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static string? Optional(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>An SDD/Harness agent's routing for this run, when one is driving it.</summary>
    /// <remarks>
    /// Three separate arguments rather than an object, because that is how the renderer sends them.
    /// A half-configured agent falls back to the task's own routing — see <see cref="AgentOverride"/>.
    /// </remarks>
    private static AgentOverride Agent(JsonElement parameters) => new(
        Optional(parameters, "agentProvider"),
        Optional(parameters, "agentModel"),
        Optional(parameters, "agentPrompt"));

    /// <summary>The findings the user picked to publish.</summary>
    /// <remarks>
    /// The only argument in this feature that is a whole array of objects, and the only one whose
    /// keys are camelCase — the renderer builds these items itself rather than passing a parameter
    /// list. An absent or empty array is not an error: a post can carry the summary alone.
    /// </remarks>
    private static IReadOnlyList<PostFindingItem> Items(JsonElement parameters) =>
        parameters.TryGetProperty("items", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Deserialize(ReviewJsonContext.Default.IReadOnlyListPostFindingItem) ?? []
            : [];

    private static bool Bool(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static long Number(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : throw new ArgumentException($"missing required parameter '{name}'");
}

/// <summary>A review-pipeline failure whose message is written to be read by a user.</summary>
/// <remarks>
/// <c>IpcServer</c> puts an exception's message straight into the JSON-RPC <c>error</c> field, so
/// these strings are a contract with the renderer exactly as <c>ProviderException</c>'s are.
/// </remarks>
public sealed class ReviewException(string message) : Exception(message);

/// <summary>The activity row's opaque metadata for a completed review.</summary>
/// <remarks>
/// camelCase keys, unlike everything else on the wire: this object is assembled by hand for the
/// activity row rather than serialised from a payload record, and the UI reads it as written.
/// </remarks>
internal sealed record ReviewJobMeta(
    [property: JsonPropertyName("prId")] long PrId,
    [property: JsonPropertyName("prTitle")] string PrTitle,
    [property: JsonPropertyName("level")] string Level);

/// <summary>Serialisable types this feature puts on the wire.</summary>
/// <remarks>
/// snake_case, because <c>renderer/src/types/domain.ts</c> declares these field names
/// verbatim — including <c>SavedFinding</c>, which mirrors <see cref="MemoryFinding"/> field for
/// field (<c>XLANG-010</c>). The same context serialises the <c>findings</c> column, so what is
/// stored and what the renderer receives can never drift apart.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ReviewJobMeta))]
[JsonSerializable(typeof(ReviewRunDetail))]
[JsonSerializable(typeof(List<ReviewRunSummary>))]
[JsonSerializable(typeof(MemoryFinding))]
[JsonSerializable(typeof(List<MemoryFinding>))]
[JsonSerializable(typeof(ReviewMeta))]
[JsonSerializable(typeof(IReadOnlyList<PostFindingItem>))]
internal sealed partial class ReviewJsonContext : JsonSerializerContext;
