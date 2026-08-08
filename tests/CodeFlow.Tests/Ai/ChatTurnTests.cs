using CodeFlow.Activity;
using CodeFlow.Ai;
using CodeFlow.Tests.Git;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// What a chat turn reads, what it records, and what it deliberately does not.
/// See <c>docs/business-rules/05-ai-engines.md</c> <c>AI-050</c>.
/// </summary>
/// <remarks>
/// <para>
/// The engine call is scripted but the <em>routing</em> is not: this is the code that reads the
/// cascade, so the config comes from the database exactly as in production. With no
/// <c>ai_provider</c> setting stored that resolves to <c>claude</c>, which is why the recorded
/// provider is <c>claude</c> and not the scripted engine's own id.
/// </para>
/// <para>
/// The project's <c>local_path</c> is not a repository in the chat tests, so the checkpoint fails and
/// is swallowed — which is the documented best-effort behaviour and, incidentally, proves it: a turn
/// still completes and is still recorded when the working tree cannot be snapshotted.
/// </para>
/// </remarks>
public sealed class ChatTurnTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_successful_turn_is_recorded_with_everything_the_reply_is_stamped_with()
    {
        using var db = new TempDatabase();
        var project = Project(db);
        var engine = ScriptedEngine.Answering("because the query is unindexed", sessionId: "sess-9", model: "opus");

        var reply = await AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "why is this slow?", sessionId: null, "conv-1", "run-1",
            NoAgent, Token);

        Assert.Equal("because the query is unindexed", reply.Text);
        Assert.Equal("sess-9", reply.SessionId);
        Assert.Equal("opus", reply.Model);
        // From the routing cascade, not from the scripted engine: no ai_provider setting means claude.
        Assert.Equal("claude", reply.Provider);

        var turn = Assert.Single(db.Use(c => ActivityLogStore.Messages(c, project, "conv-1")));

        Assert.Equal("why is this slow?", turn.Question);
        Assert.Equal("because the query is unindexed", turn.Answer);
        Assert.False(turn.IsError);
        Assert.Equal("claude", turn.Provider);
        Assert.Equal("opus", turn.Model);
        // The engine's own token, not the app's conversation id, which lives in session_id.
        Assert.Equal("sess-9", turn.EngineSessionId);
        Assert.Equal("conv-1", turn.SessionId);
        // The timestamp shown live is the one the persisted row carries.
        Assert.Equal(turn.CreatedAt, reply.CreatedAt);
    }

    [Fact]
    public async Task A_failed_turn_is_recorded_so_the_error_outlives_the_next_message()
    {
        using var db = new TempDatabase();
        var project = Project(db);
        var engine = ScriptedEngine.Failing("QUOTA_EXCEEDED::out of credit");

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "why is this slow?", sessionId: null, "conv-1", "run-1",
            NoAgent, Token));

        Assert.Equal("QUOTA_EXCEEDED::out of credit", failure.Message);

        var turn = Assert.Single(db.Use(c => ActivityLogStore.Messages(c, project, "conv-1")));

        Assert.True(turn.IsError);
        // Raw, marker and all, so a reopened conversation can re-derive the billing notice.
        Assert.Equal("QUOTA_EXCEEDED::out of credit", turn.Answer);
        // Which engine failed is what makes it diagnosable; there is no model, because the CLI never
        // got that far.
        Assert.Equal("claude", turn.Provider);
        Assert.Null(turn.Model);
    }

    [Fact]
    public async Task A_turn_the_user_stopped_is_not_history_at_all()
    {
        // AI-050. Filing it would leave a permanent failed turn in the transcript for something they
        // did on purpose.
        using var db = new TempDatabase();
        var project = Project(db);
        var engine = ScriptedEngine.Failing(AiRunRegistry.CancelledMarker);

        await Assert.ThrowsAsync<AiRunFailedException>(() => AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "never mind", sessionId: null, "conv-1", "run-1", NoAgent, Token));

        Assert.Empty(db.Use(c => ActivityLogStore.Messages(c, project, "conv-1")));
        Assert.Empty(db.Use(c => ActivityLogStore.Conversations(c, project, null)));
    }

    [Fact]
    public async Task A_caller_that_names_no_conversation_still_gets_its_turn_recorded()
    {
        // The fallback only matters for an older frontend: a throwaway id keeps the turn as its own
        // single-turn activity rather than silently losing it.
        using var db = new TempDatabase();
        var project = Project(db);
        var engine = ScriptedEngine.Answering("an answer");

        await AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "a question", sessionId: null, conversationId: null, "run-1",
            NoAgent, Token);

        var conversation = Assert.Single(db.Use(c => ActivityLogStore.Conversations(c, project, null)));
        Assert.StartsWith("conv-", conversation.SessionId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resume_token_survives_a_second_turn_on_the_same_provider()
    {
        using var db = new TempDatabase();
        var project = Project(db);
        var engine = ScriptedEngine.Answering("an answer", sessionId: "sess-1");

        await AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "first", sessionId: null, "conv-1", "run-1", NoAgent, Token);

        await AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "second", "sess-1", "conv-1", "run-2", NoAgent, Token);

        Assert.Equal("sess-1", engine.Invocations[1].ResumeSessionId);
    }

    [Fact]
    public async Task A_resume_token_minted_by_another_provider_is_dropped()
    {
        // Session tokens are not portable: replaying one into a different engine either fails
        // outright or silently continues something unrelated and answers with the wrong context.
        using var db = new TempDatabase();
        var project = Project(db);

        db.Use(c => ActivityLogStore.Add(
            c, project, "conv-1", "sess-codex", "earlier", "an answer", trace: null,
            new TurnMeta("codex", "gpt-5", "0.20", 10), isError: false));

        // This turn routes to claude, so the token the previous one left behind is not replayable.
        var engine = ScriptedEngine.Answering("an answer");
        await AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "next", "sess-codex", "conv-1", "run-1", NoAgent, Token);

        Assert.Null(engine.Only.ResumeSessionId);

        // Dropping the token also re-establishes the project context, which the new engine has never
        // been given.
        Assert.Equal(Prompts.DefaultChatSystemPrompt, engine.Only.SystemPrompt);
    }

    [Fact]
    public async Task A_conversation_with_no_recorded_provider_keeps_its_token()
    {
        // Anything that cannot be determined keeps the token: discarding a working session is the
        // worse failure.
        using var db = new TempDatabase();
        var project = Project(db);
        var engine = ScriptedEngine.Answering("an answer");

        await AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "next", "sess-unknown", "conv-never-seen", "run-1", NoAgent, Token);

        Assert.Equal("sess-unknown", engine.Only.ResumeSessionId);
    }

    [Fact]
    public async Task An_enabled_context_reaches_the_turn_and_a_disabled_one_does_not()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#6366f1"));
        var project = db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id))).Id;

        db.Do(c =>
        {
            ReviewContextStore.Upsert(c, null, workspace.Id, "Conventions", "two-space indent", enabled: true);
            ReviewContextStore.Upsert(c, null, workspace.Id, "Retired", "ignore this", enabled: false);
        });

        var engine = ScriptedEngine.Answering("an answer");
        await AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "a question", sessionId: null, "conv-1", "run-1", NoAgent, Token);

        Assert.Contains("- Conventions: two-space indent", engine.Only.StdinContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Retired", engine.Only.StdinContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_active_agents_prompt_frames_the_turn_ahead_of_every_context()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#6366f1"));
        var project = db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id))).Id;

        db.Do(c => ReviewContextStore.Upsert(c, null, workspace.Id, "Conventions", "two-space", enabled: true));

        var engine = ScriptedEngine.Answering("an answer");
        await AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, project, "a question", sessionId: null, "conv-1", "run-1",
            new AgentOverride(Provider: null, Model: null, Prompt: "You are the reviewer."), Token);

        var payload = engine.Only.StdinContent;
        Assert.Contains("- Agent: You are the reviewer.", payload, StringComparison.Ordinal);
        Assert.True(
            payload.IndexOf("- Agent:", StringComparison.Ordinal)
            < payload.IndexOf("- Conventions:", StringComparison.Ordinal),
            "the agent's instructions must come first");
    }

    [Fact]
    public async Task An_unknown_project_is_refused_before_any_engine_is_reached()
    {
        using var db = new TempDatabase();
        var engine = ScriptedEngine.Answering("an answer");

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiTurn.SendChatMessageAsync(
            db.Handle, engine.Runner, "no-such-project", "a question", null, "conv-1", "run-1", NoAgent, Token));

        Assert.Equal("Project not found", failure.Message);
        Assert.Empty(engine.Invocations);
    }

    // ---------- the analysis, which files a job instead of a turn ----------
    //
    // These need a real repository: the diff of the working tree is the input, so a fake path would
    // fail before reaching anything under test.

    [Fact]
    public async Task A_finished_analysis_is_filed_under_the_job_id_the_ui_already_renders()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "before\n");
        repo.Commit("initial", "a.txt");
        repo.Write("a.txt", "after\n");

        using var db = new TempDatabase();
        var project = Project(db, repo.Path);
        var engine = ScriptedEngine.Answering("📈 CALIDAD: Fiabilidad=A");

        var text = await AiTurn.AnalyzeWorkingChangesAsync(
            db.Handle, engine.Runner, project, "job-1", NoAgent, Token);

        // The diff reaches the engine as the rendered text, not as a structure.
        Assert.Contains("--- a.txt (modified)", engine.Only.StdinContent, StringComparison.Ordinal);
        Assert.Contains("+after", engine.Only.StdinContent, StringComparison.Ordinal);

        var job = Assert.Single(db.Use(c => JobHistoryStore.List(c, project)));

        Assert.Equal("job-1", job.Id);
        Assert.Equal("analyze-changes", job.Kind);
        Assert.Equal("Análisis de cambios", job.Label);
        Assert.Equal("done", job.Status);
        Assert.Equal(text, job.Result);
        Assert.Null(job.Error);
        Assert.Equal("{}", job.Meta);
    }

    [Fact]
    public async Task A_failed_analysis_is_filed_as_an_error_so_it_is_still_there_tomorrow()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "content\n");

        using var db = new TempDatabase();
        var project = Project(db, repo.Path);
        var engine = ScriptedEngine.Failing("codex exited with an error (exit status: 1): no such model");

        await Assert.ThrowsAsync<AiRunFailedException>(() => AiTurn.AnalyzeWorkingChangesAsync(
            db.Handle, engine.Runner, project, "job-1", NoAgent, Token));

        var job = Assert.Single(db.Use(c => JobHistoryStore.List(c, project)));

        Assert.Equal("error", job.Status);
        Assert.Equal("codex exited with an error (exit status: 1): no such model", job.Error);
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task A_cancelled_analysis_writes_no_job_row()
    {
        // AI-051, the same rule as a cancelled chat turn: an intentional stop is not a red row.
        using var repo = new TempRepo();
        repo.Write("a.txt", "content\n");

        using var db = new TempDatabase();
        var project = Project(db, repo.Path);
        var engine = ScriptedEngine.Failing(AiRunRegistry.CancelledMarker);

        await Assert.ThrowsAsync<AiRunFailedException>(() => AiTurn.AnalyzeWorkingChangesAsync(
            db.Handle, engine.Runner, project, "job-1", NoAgent, Token));

        Assert.Empty(db.Use(c => JobHistoryStore.List(c, project)));
    }

    [Fact]
    public async Task An_analysis_of_a_clean_working_tree_is_refused_and_writes_no_job_row()
    {
        // This used to assert the opposite — "an ordinary failed run: the user asked for an analysis
        // and there is a red row explaining why there isn't one". That premise stopped holding when
        // the analyze tab began starting a run on *open*: a clean tree then filed a permanent error
        // row for a request nobody made. The refusal now carries a marker the renderer reads as an
        // empty state, and joins the cancelled run in writing nothing.
        using var repo = new TempRepo();
        repo.Write("a.txt", "content\n");
        repo.Commit("initial", "a.txt");

        using var db = new TempDatabase();
        var project = Project(db, repo.Path);
        var engine = ScriptedEngine.Answering("never reached");

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiTurn.AnalyzeWorkingChangesAsync(
            db.Handle, engine.Runner, project, "job-1", NoAgent, Token));

        Assert.Equal(
            AiOperations.NothingToAnalyzePrefix + "No hay cambios sin commitear para analizar",
            failure.Message);
        Assert.Empty(engine.Invocations);
        Assert.Empty(db.Use(c => JobHistoryStore.List(c, project)));
    }

    private static AgentOverride NoAgent => new(null, null, null);

    private static string Project(TempDatabase db, string? localPath = null)
    {
        var workspace = db.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#6366f1"));
        var input = WorkspaceStoreTests.NewProjectIn(workspace.Id);

        if (localPath is not null)
        {
            input = input with { LocalPath = localPath };
        }

        return db.Use(c => ProjectStore.Create(c, input)).Id;
    }
}
