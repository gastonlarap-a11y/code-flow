using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Git;
using CodeFlow.Ipc;
using CodeFlow.Storage;
using CodeFlow.Workspaces;

namespace CodeFlow.Ai;

/// <summary>
/// The AI commands that exist so far.
/// See <c>docs/business-rules/05-ai-engines.md</c>.
/// </summary>
/// <remarks>
/// Cancellation, provider discovery and the built-in templates. The eight high-level operations —
/// commit messages, analysis, chat, conflict resolution, inline edit — arrive with the slice that
/// wires the run lifecycle to them; what is here is everything they will need underneath.
/// </remarks>
public static class AiCommands
{
    public static CommandRegistry AddAiCommands(
        this CommandRegistry registry, AiRunRegistry runs, Database database, HttpClient http) =>
        registry
            .Add("cancel_ai_run", (parameters, _) =>
            {
                var runId = parameters.TryGetProperty("runId", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : throw new ArgumentException("missing required parameter 'runId'");

                // Returns whether a run with that id was in flight — not whether it has finished
                // dying. Waiting for subprocess cleanup would stall the UI on something 1.7.2
                // never made it wait for.
                var cancelled = runs.Cancel(runId);
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(
                    JsonSerializer.SerializeToUtf8Bytes(cancelled, AiJsonContext.Default.Boolean));
            })
            .Add("list_ai_models", async (parameters, cancellationToken) =>
            {
                // An absent or blank provider means "whichever is active", so the settings screen
                // can ask about the current one without first reading it.
                var requested = Optional(parameters, "provider");
                var (engine, binary) = await ResolveAsync(database, requested, cancellationToken).ConfigureAwait(false);

                var models = await ModelDiscovery.ListAsync(engine, binary, http, cancellationToken)
                    .ConfigureAwait(false);

                return Json(models, AiJsonContext.Default.IReadOnlyListString);
            })
            .Add("check_ai_provider", async (parameters, cancellationToken) =>
            {
                var provider = Arg(parameters, "provider");
                var (engine, binary) = await ResolveAsync(database, provider, cancellationToken).ConfigureAwait(false);

                var status = await ModelDiscovery.ProbeAsync(engine, binary, http, cancellationToken)
                    .ConfigureAwait(false);

                return Json(status, AiJsonContext.Default.ProviderStatus);
            })
            // The built-in templates, verbatim, for the settings editor's "restore default".
            .Add("default_commit_template", Constant(Prompts.DefaultCommitTemplate))
            .Add("default_review_template", Constant(Prompts.DefaultReviewPrompt))
            .Add("default_analyze_template", Constant(Prompts.DefaultAnalyzeTemplate))
            .Add("default_pr_description_template", Constant(Prompts.DefaultPrDescriptionTemplate))
            .Add("default_resolve_conflict_template", Constant(Prompts.DefaultResolveConflictTemplate))
            // ---------- the operations ----------
            .Add("generate_commit_message", async (parameters, cancellationToken) =>
            {
                var diff = Arg(parameters, "diff");
                var (config, template) = await database.ReadAsync(
                    connection => (
                        AiRouting.Resolve(connection, "commit"),
                        AiRouting.SharedTemplate(connection, "commit_template")),
                    cancellationToken).ConfigureAwait(false);

                var message = await AiOperations.GenerateCommitMessageAsync(
                    Runner(runs, http), config, diff, template, Run(parameters), cancellationToken)
                    .ConfigureAwait(false);

                return Json(message, AiJsonContext.Default.String);
            })
            .Add("resolve_conflict_with_ai", async (parameters, cancellationToken) =>
            {
                var repoPath = Arg(parameters, "repoPath");
                var relativePath = Arg(parameters, "relPath");

                var (config, template) = await database.ReadAsync(
                    connection => (
                        AiRouting.Resolve(connection, "conflict"),
                        AiRouting.SharedTemplate(connection, "resolve_conflict_template")),
                    cancellationToken).ConfigureAwait(false);

                // Off the pump thread: reading the three index stages is synchronous LibGit2Sharp.
                var versions = await Task.Run(() => Merge.Versions(repoPath, relativePath), cancellationToken)
                    .ConfigureAwait(false);

                var resolved = await AiOperations.ResolveConflictAsync(
                    Runner(runs, http), config, relativePath, versions.Base, versions.Ours, versions.Theirs,
                    template, Run(parameters), cancellationToken).ConfigureAwait(false);

                return Json(resolved, AiJsonContext.Default.String);
            })
            .Add("inline_edit_with_ai", async (parameters, cancellationToken) =>
            {
                var relativePath = Arg(parameters, "relPath");
                var fileContent = Arg(parameters, "fileContent");
                var selection = Arg(parameters, "selection");
                var instruction = Arg(parameters, "instruction");

                var config = await database
                    .ReadAsync(connection => AiRouting.Resolve(connection, "inline"), cancellationToken)
                    .ConfigureAwait(false);

                var rewritten = await AiOperations.InlineEditAsync(
                    Runner(runs, http), config, relativePath, fileContent, selection, instruction,
                    Run(parameters), cancellationToken).ConfigureAwait(false);

                return Json(rewritten, AiJsonContext.Default.String);
            })
            .Add("resolve_finding_with_ai", async (parameters, cancellationToken) =>
            {
                var projectId = Arg(parameters, "projectId");
                var findingPrompt = Arg(parameters, "findingPrompt");

                var (project, config) = await database.ReadAsync(
                    connection =>
                    {
                        var found = ProjectStore.Get(connection, projectId)
                            ?? throw new AiRunFailedException("Project not found");
                        return (found, AiRouting.Resolve(connection, "fix"));
                    },
                    cancellationToken).ConfigureAwait(false);

                var checkpoint = AiTurn.CheckpointBefore(project.LocalPath, "fix-finding");
                try
                {
                    var summary = await AiOperations.ApplyFindingFixAsync(
                        Runner(runs, http), config, findingPrompt, project.LocalPath, Run(parameters),
                        cancellationToken).ConfigureAwait(false);

                    return Json(summary, AiJsonContext.Default.String);
                }
                finally
                {
                    // Also after a failure or a cancellation: an agent killed mid-edit is exactly when
                    // a half-applied fix needs undoing.
                    AiTurn.CheckpointAfter(project.LocalPath, checkpoint);
                }
            })
            // `analyze_working_changes` used to be registered here. It is now one half of
            // `review_changes` (`Tickets/TicketCommands.cs`), which carries the two axes the panel
            // exposes; the body it called still lives in `AiTurn`, refusal rules and all.
            .Add("send_chat_message", async (parameters, cancellationToken) =>
            {
                var reply = await AiTurn.SendChatMessageAsync(
                    database,
                    Runner(runs, http),
                    Arg(parameters, "projectId"),
                    Arg(parameters, "message"),
                    Optional(parameters, "sessionId"),
                    Optional(parameters, "conversationId"),
                    Optional(parameters, "runId"),
                    Agent(parameters),
                    cancellationToken).ConfigureAwait(false);

                return Json(reply, AiJsonContext.Default.ChatReply);
            });

    /// <summary>The runner the operations invoke, bound to this process's registry and HTTP client.</summary>
    private static AiRunner Runner(AiRunRegistry runs, HttpClient http) => AiEngineRunner.Bind(runs, http);

    /// <summary>
    /// The run this command belongs to, from the id the frontend minted before invoking.
    /// </summary>
    /// <remarks>
    /// Absent or blank means untracked: no <c>ai:output</c> events and no stop button, which is what
    /// 1.7.2's <c>None</c> run id does.
    /// </remarks>
    private static AiRunContext? Run(JsonElement parameters) =>
        Optional(parameters, "runId") is { } runId && !string.IsNullOrWhiteSpace(runId)
            ? new AiRunContext(runId)
            : null;

    /// <summary>The active workspace agent's routing for this run, if the renderer sent one.</summary>
    private static AgentOverride Agent(JsonElement parameters) => new(
        Optional(parameters, "agentProvider"),
        Optional(parameters, "agentModel"),
        Optional(parameters, "agentPrompt"));

    /// <summary>Resolves a provider to its engine and the binary or endpoint to use.</summary>
    /// <remarks>
    /// The manual <c>{provider}_binary_path</c> setting wins over the engine's default, blank
    /// counting as unset (<c>AI-007</c>).
    /// </remarks>
    private static async ValueTask<(IAiEngine Engine, string Binary)> ResolveAsync(
        Database database, string? requested, CancellationToken cancellationToken)
    {
        return await database.ReadAsync(connection =>
        {
            var provider = string.IsNullOrWhiteSpace(requested)
                ? AiRouting.ProviderFor(connection, "chat")
                : requested;

            var engine = EngineCatalog.EngineFor(provider);
            var configured = Settings.GetSetting(connection, $"{provider}_binary_path");

            return (engine, string.IsNullOrWhiteSpace(configured) ? engine.DefaultBinary : configured);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A command that always answers with the same text.</summary>
    private static CommandHandler Constant(string value) =>
        (_, _) => ValueTask.FromResult(Json(value, AiJsonContext.Default.String));

    private static ReadOnlyMemory<byte> Json<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToUtf8Bytes(value, type);

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static string? Optional(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
