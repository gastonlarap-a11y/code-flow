using System.Diagnostics;
using System.Text.Json;
using CodeFlow.Activity;
using CodeFlow.Git;
using CodeFlow.Platform;
using CodeFlow.Storage;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Ai;

/// <summary>An active workspace agent's own routing and instructions for one run.</summary>
/// <remarks>
/// All three arrive as separate command arguments rather than as an object, because that is how the
/// renderer sends them. Provider and model are only honoured together: a half-configured agent falls
/// back to the task's normal routing rather than running on a model the user never chose.
/// </remarks>
internal sealed record AgentOverride(string? Provider, string? Model, string? Prompt)
{
    /// <summary>Whether this override replaces the task's routing.</summary>
    public bool RoutesItself =>
        !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>What one repository-scoped AI run reads before it starts.</summary>
internal sealed record TurnSetup(
    Project Project,
    AiConfig Config,
    List<(string Name, string Content)> Contexts,
    List<WorkspaceMcp> Mcps);

/// <summary>
/// The two repository-scoped AI runs.
/// </summary>
/// <remarks>
/// <para>
/// Chat and the pre-commit analysis are the only operations that read a project's whole
/// configuration — its enabled review contexts, its MCP servers, an active agent's routing — and the
/// only two that persist what they produced. That shared shape is why they sit together here rather
/// than inline in <see cref="AiCommands"/>, which stays a registration table.
/// </para>
/// <para>
/// Both operations sync the workspace's skills into the project first, best-effort, from
/// <see cref="Prepare"/> — see <see cref="Workspaces.SkillSync"/> for what that does and does not
/// touch.
/// </para>
/// </remarks>
internal static class AiTurn
{
    /// <summary>Everything the reply is stamped with, mirroring 1.7.2's <c>ChatReply</c>.</summary>
    /// <param name="Model">
    /// The model that actually answered, when the CLI reported one. Null lets the chat's chip fall
    /// back to the configured setting.
    /// </param>
    /// <param name="Provider">
    /// Reported back rather than read from the setting on the frontend, so the stamp under a reply
    /// keeps naming the engine that actually ran even after the routing is changed.
    /// </param>
    /// <param name="CreatedAt">
    /// From the persisted row, so the timestamp shown live and the one shown on a reopened
    /// conversation are the same instant.
    /// </param>
    public sealed record ChatReply(
        string Text,
        string? SessionId,
        string? Model,
        string Provider,
        string? EngineVersion,
        string CreatedAt,
        long ResponseTimeMs);

    /// <summary>The label a pre-commit analysis is filed under. `VERBATIM`.</summary>
    /// <remarks>
    /// Spanish, and written by the backend rather than the frontend — unlike a checkpoint's
    /// <c>kind</c>, which is a translation key. Reproduced as-is: it is the text already sitting in
    /// every existing user's <c>job_history</c>, and changing it would split one kind of row into two
    /// in the list.
    /// </remarks>
    private const string AnalysisLabel = "Análisis de cambios";

    /// <summary>One turn of an open-ended chat, recorded whatever the outcome.</summary>
    public static async Task<ChatReply> SendChatMessageAsync(
        Database database,
        AiRunner runner,
        string projectId,
        string message,
        string? sessionId,
        string? conversationId,
        string? runId,
        AgentOverride agent,
        CancellationToken cancellationToken)
    {
        // Every turn files under the conversation the frontend named. The fallback only matters for a
        // caller that supplied none: it mints a throwaway id so the turn is still recorded, as its
        // own single-turn activity, rather than silently lost.
        var conversation = string.IsNullOrWhiteSpace(conversationId)
            ? $"conv-{Guid.NewGuid()}"
            : conversationId;

        var (setup, resumeSession) = await database.ReadAsync(
            connection =>
            {
                var prepared = Prepare(connection, projectId, "chat", agent);

                // Shadows the argument on purpose: nothing below should see the unvalidated token,
                // and the turn is recorded against the session it actually ran under.
                var session = SessionForProvider(
                    connection, projectId, conversationId, sessionId, prepared.Config.Provider);

                return (prepared, session);
            },
            cancellationToken).ConfigureAwait(false);

        var config = setup.Config;
        var mcpConfigPath = McpConfig.Write(setup.Mcps, AppPaths.WorkspaceMcpConfigFile(setup.Project.WorkspaceId));
        var run = Context(runId);

        // The chat runs with edits auto-approved, so it can and does touch files — it gets the same
        // undo protection as an explicit "fix with AI".
        var checkpoint = CheckpointBefore(setup.Project.LocalPath, "chat");

        // Timed around the engine call only, so it reflects how long the model actually took — not
        // the surrounding database reads or the IPC hop.
        var started = Stopwatch.StartNew();
        AiRun? result = null;
        AiRunFailedException? failure = null;
        try
        {
            result = await AiOperations.ChatAsync(
                runner, config, setup.Contexts, message, resumeSession,
                setup.Project.LocalPath, mcpConfigPath, run, cancellationToken).ConfigureAwait(false);
        }
        catch (AiRunFailedException caught)
        {
            failure = caught;
        }

        var elapsed = (long)started.Elapsed.TotalMilliseconds;
        CheckpointAfter(setup.Project.LocalPath, checkpoint);

        // Read after the run, not before: it is cached per binary, so only the very first turn of an
        // app session pays for the probe, and it never sits between the user pressing send and the
        // engine starting.
        var engineVersion = await ModelDiscovery
            .EngineVersionAsync(config.Engine, config.BinaryPath, cancellationToken).ConfigureAwait(false);

        // Kept with the turn so the answer can still show *how* it was reached — which files were
        // read, which commands ran — long after the live log is gone.
        var trace = Trace(run);

        if (failure is not null)
        {
            // A run the user stopped is not history: it has no answer, and filing it would leave a
            // permanent failed turn in the transcript for something they did on purpose. Other
            // failures are recorded, or the panel's error would vanish the moment the next message is
            // sent and days later nothing would explain why a run died.
            if (!failure.Message.StartsWith(AiRunRegistry.CancelledMarker, StringComparison.Ordinal))
            {
                await database.WriteAsync(
                    connection => ActivityLogStore.Add(
                        connection, projectId, conversation, resumeSession, message, failure.Message, trace,
                        // A failed turn has no model to report — the CLI never got that far — but
                        // *which* engine and version failed is exactly what makes it diagnosable.
                        new TurnMeta(config.Provider, Model: null, engineVersion, elapsed),
                        isError: true),
                    cancellationToken).ConfigureAwait(false);
            }

            throw failure;
        }

        var answer = result!;
        string createdAt;
        try
        {
            var entry = await database.WriteAsync(
                connection => ActivityLogStore.Add(
                    connection, projectId, conversation, answer.SessionId, message, answer.Text, trace,
                    new TurnMeta(config.Provider, answer.Model, engineVersion, elapsed),
                    isError: false),
                cancellationToken).ConfigureAwait(false);

            createdAt = entry.CreatedAt;
        }
        catch (SqliteException)
        {
            // The reply is already in hand; a failed *write* should not cost the user their answer, so
            // the turn is still returned — just stamped with the time it arrived rather than the time
            // it was filed.
            createdAt = Clock.Now();
        }

        return new ChatReply(
            answer.Text, answer.SessionId, answer.Model, config.Provider, engineVersion, createdAt, elapsed);
    }

    /// <summary>
    /// Scans local changes for bugs, without a work item in the question.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The job id doubles as the run id: the job row the UI already renders is exactly the thing that
    /// should show this run's live output and its stop button.
    /// </para>
    /// <para>
    /// <paramref name="scope"/> is the only thing that made this more general than its old name
    /// suggested — <see cref="ReviewScope.Branch"/> is the combination the panel could not offer
    /// before, and it is the useful one in a repository that keeps no tickets. Everything else here
    /// is unchanged, <c>AI-024</c>'s refusal handling included.
    /// </para>
    /// </remarks>
    public static async Task<string> AnalyzeChangesAsync(
        Database database,
        AiRunner runner,
        string projectId,
        string jobId,
        ReviewScope scope,
        string baseRef,
        AgentOverride agent,
        CancellationToken cancellationToken)
    {
        var (setup, template) = await database.ReadAsync(
            connection => (
                Prepare(connection, projectId, "analyze", agent),
                AiRouting.SharedTemplate(connection, "analyze_template")),
            cancellationToken).ConfigureAwait(false);

        var mcpConfigPath = McpConfig.Write(setup.Mcps, AppPaths.WorkspaceMcpConfigFile(setup.Project.WorkspaceId));

        // Off the pump thread: LibGit2Sharp is synchronous, and a full-context diff of a large
        // working tree is not a bounded amount of work.
        var diff = await Task
            .Run(
                () => Diff.RenderForPrompt(scope is ReviewScope.Branch
                    ? Diff.BranchContribution(setup.Project.LocalPath, baseRef)
                    : Diff.Working(setup.Project.LocalPath)),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var text = await AiOperations.AnalyzeChangesAsync(
                runner, setup.Config, setup.Contexts, diff, scope, setup.Project.LocalPath, template,
                mcpConfigPath, new AiRunContext(jobId), DateTimeOffset.Now, cancellationToken)
                .ConfigureAwait(false);

            await database.WriteAsync(
                connection => JobHistoryStore.Add(
                    connection, jobId, projectId, "analyze-changes", AnalysisLabel, "done", text, null, "{}"),
                cancellationToken).ConfigureAwait(false);

            return text;
        }
        catch (AiRunFailedException failure)
        {
            // Two refusals that are not history worth keeping, for the same reason: neither is
            // something the user asked for, and filing either leaves a permanent red row.
            //
            // A run the user stopped is the original case — they did that on purpose. "Nothing
            // uncommitted to analyse" joins it because the analyze tab starts a run when it is
            // merely *opened*: on a clean tree that filed an error for a request nobody made, which
            // is what `AI-024` used to call "an ordinary failed run" back when reaching this needed
            // a deliberate click.
            var refused =
                failure.Message.StartsWith(AiRunRegistry.CancelledMarker, StringComparison.Ordinal)
                || failure.Message.StartsWith(AiOperations.NothingToAnalyzePrefix, StringComparison.Ordinal);

            if (!refused)
            {
                await database.WriteAsync(
                    connection => JobHistoryStore.Add(
                        connection, jobId, projectId, "analyze-changes", AnalysisLabel, "error", null,
                        failure.Message, "{}"),
                    cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Reads the project, its configuration and the routing one run will use.</summary>
    /// <remarks>
    /// An active agent's prompt goes in <em>first</em>, ahead of every workspace review context, so
    /// the role frames the whole turn.
    /// </remarks>
    public static TurnSetup Prepare(
        SqliteConnection connection, string projectId, string task, AgentOverride agent)
    {
        var project = ProjectStore.Get(connection, projectId)
            ?? throw new AiRunFailedException("Project not found");

        var contexts = ReviewContextStore.List(connection, project.WorkspaceId)
            .Where(context => context.Enabled)
            .Select(context => (context.Name, context.Content))
            .ToList();

        if (!string.IsNullOrWhiteSpace(agent.Prompt))
        {
            contexts.Insert(0, ("Agent", agent.Prompt));
        }

        // The task travels down the agent's route too: without it a workspace agent driving an
        // analysis skipped the toolset default and ran unbounded, which is the whole point of having
        // one.
        var config = agent.RoutesItself
            ? AiRouting.ResolveFor(connection, agent.Provider!, agent.Model!, task)
            : AiRouting.Resolve(connection, task);

        // The workspace's enabled skills are copied into the project before the engine starts,
        // because Claude Code only discovers skills relative to its working directory. Best-effort
        // and never a reason to refuse the run — 1.7.2 calls it as `let _ = ...`.
        SkillSync.TryRun(
            SkillStore.List(connection, project.WorkspaceId),
            SkillFiles.RootFor(project.WorkspaceId),
            project.LocalPath);

        return new TurnSetup(project, config, contexts, WorkspaceMcpStore.List(connection, project.WorkspaceId));
    }

    /// <summary>
    /// Drops a resume token minted by a <em>different</em> engine than the one about to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session tokens are not portable across providers, and each engine namespaces them differently
    /// — a Claude UUID, an opencode <c>ses_…</c>, a Codex rollout UUID — while Gemini hands back a
    /// fixed "continue your last run" sentinel. Replaying any of them into a different engine either
    /// fails outright or, worse, silently continues something unrelated and answers with the wrong
    /// context. Returning null makes the turn open a fresh engine session, which also re-sends the
    /// project context.
    /// </para>
    /// <para>
    /// This is only about crossing <em>between</em> providers; two conversations on the same provider
    /// are kept apart by the engines themselves, by resuming a specific id rather than "the last
    /// run". Anything that cannot be determined — no recorded provider, a read that failed — keeps
    /// the token, because discarding a working session is the worse failure.
    /// </para>
    /// </remarks>
    private static string? SessionForProvider(
        SqliteConnection connection,
        string projectId,
        string? conversationId,
        string? sessionId,
        string provider)
    {
        if (sessionId is null || conversationId is null)
        {
            return sessionId;
        }

        return ActivityLogStore.LastTurnProvider(connection, projectId, conversationId) is { } previous
               && previous != provider
            ? null
            : sessionId;
    }

    /// <summary>The run's recorded output as the JSON the renderer rehydrates, or null when empty.</summary>
    private static string? Trace(AiRunContext? run)
    {
        if (run is null || run.Trace.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(run.Trace, AiJsonContext.Default.IReadOnlyListTraceLine);
    }

    /// <summary>An untracked run when the caller sent no id, which is what 1.7.2's <c>None</c> means.</summary>
    private static AiRunContext? Context(string? runId) =>
        string.IsNullOrWhiteSpace(runId) ? null : new AiRunContext(runId);

    /// <summary>
    /// Snapshots the working tree before an AI action that can write to it, so the run is undoable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Best-effort by design: a repository that cannot be snapshotted — no HEAD yet, an unreadable
    /// index, a path that has since been deleted — must not block the action the user actually asked
    /// for. They just do not get the undo button.
    /// </para>
    /// <para>
    /// The caught set is wide on purpose. CodeFlow 1.7.2 maps <em>every</em> snapshot failure to "no
    /// checkpoint", and narrowing it here would turn a stale project path into a failed chat turn,
    /// which is the exact outcome this is meant to prevent.
    /// </para>
    /// </remarks>
    public static string? CheckpointBefore(string repoPath, string kind)
    {
        try
        {
            return Checkpoints.Create(repoPath, kind);
        }
        catch (Exception failure) when (IsSnapshotFailure(failure))
        {
            Console.Error.WriteLine($"checkpoint before '{kind}' failed: {failure.Message}");
            return null;
        }
    }

    /// <summary>Discards a checkpoint whose run turned out to change nothing on disk.</summary>
    /// <remarks>
    /// An "undo" that would restore zero files is just clutter in the list. Runs after failures and
    /// cancellations too: an agent killed mid-edit is exactly when a half-applied change needs
    /// undoing, so the checkpoint only goes away if nothing moved.
    /// </remarks>
    public static void CheckpointAfter(string repoPath, string? checkpointId)
    {
        if (checkpointId is null)
        {
            return;
        }

        try
        {
            Checkpoints.RemoveIfUnchanged(repoPath, checkpointId);
        }
        catch (Exception failure) when (IsSnapshotFailure(failure))
        {
            // The snapshot stays in the list. Harmless, and the user can delete it.
        }
    }

    private static bool IsSnapshotFailure(Exception failure) =>
        failure is LibGit2Sharp.LibGit2SharpException
            or IOException
            or ArgumentException
            or UnauthorizedAccessException;
}
