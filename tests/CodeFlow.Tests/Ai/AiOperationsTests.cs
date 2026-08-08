using CodeFlow.Ai;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// The provider-neutral operations: what they build, and what they refuse.
/// See <c>docs/business-rules/05-ai-engines.md</c>.
/// </summary>
public sealed class AiOperationsTests
{
    private static readonly (string Name, string Content)[] Contexts =
    [
        ("Conventions", "two-space indent"),
        ("Domain", "orders are immutable"),
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------- commit messages ----------

    [Fact]
    public async Task A_commit_message_from_an_empty_diff_is_refused_before_anything_is_spawned()
    {
        var engine = ScriptedEngine.Answering("feat: something");

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiOperations.GenerateCommitMessageAsync(
            engine.Runner, engine.Config(), "   \n  ", string.Empty, run: null, Token));

        Assert.Equal("No staged changes to summarize", failure.Message);
        Assert.Empty(engine.Invocations);
    }

    [Fact]
    public async Task A_blank_template_falls_back_to_the_built_in_and_a_stored_one_wins()
    {
        var builtin = ScriptedEngine.Answering("ok");
        await AiOperations.GenerateCommitMessageAsync(
            builtin.Runner, builtin.Config(), "diff --git a b", "   ", run: null, Token);

        Assert.Equal(Prompts.DefaultCommitTemplate, builtin.Only.Prompt);

        var custom = ScriptedEngine.Answering("ok");
        await AiOperations.GenerateCommitMessageAsync(
            custom.Runner, custom.Config(), "diff --git a b", "Write it in Klingon", run: null, Token);

        Assert.Equal("Write it in Klingon", custom.Only.Prompt);
    }

    [Fact]
    public async Task A_commit_message_carries_no_footer()
    {
        // It goes into the commit box, where a stamp would land in the repository's history.
        var engine = ScriptedEngine.Answering("feat(api): add the thing");

        var message = await AiOperations.GenerateCommitMessageAsync(
            engine.Runner, engine.Config(), "diff", string.Empty, run: null, Token);

        Assert.Equal("feat(api): add the thing", message);
    }

    [Fact]
    public async Task A_commit_message_wrapped_in_a_fence_arrives_unwrapped()
    {
        // The template now allows a summary plus a bulleted body, and a multi-line answer is what a
        // model tends to fence despite being told not to — those backticks would be committed.
        var engine = ScriptedEngine.Answering("```\nfeat(api): ✨ add the thing\n\n- because it was asked for\n```");

        var message = await AiOperations.GenerateCommitMessageAsync(
            engine.Runner, engine.Config(), "diff", string.Empty, run: null, Token);

        Assert.Equal("feat(api): ✨ add the thing\n\n- because it was asked for", message);
    }

    [Theory]
    [InlineData("feat", "✨")]
    [InlineData("fix", "🐛")]
    [InlineData("docs", "📝")]
    [InlineData("style", "🎨")]
    [InlineData("refactor", "♻️")]
    [InlineData("perf", "⚡️")]
    [InlineData("test", "✅")]
    [InlineData("build", "📦️")]
    [InlineData("ci", "👷")]
    [InlineData("chore", "🔧")]
    [InlineData("revert", "⏪️")]
    public void The_built_in_commit_template_pairs_every_conventional_type_with_its_emoji(
        string type, string emoji)
    {
        // The eleven types of @commitlint/config-conventional, each with its gitmoji. Asserted per
        // pair rather than as one blob: dropping a row while rewording the template is the failure
        // this catches, and it is invisible until someone commits that kind of change.
        var template = Prompts.DefaultCommitTemplate;

        var line = Assert.Single(
            template.Split('\n'),
            l => l.StartsWith(type + " ", StringComparison.Ordinal));

        Assert.Contains(emoji, line, StringComparison.Ordinal);
    }

    // ---------- analysis ----------

    [Fact]
    public async Task An_analysis_with_nothing_uncommitted_is_refused_in_spanish_behind_a_marker()
    {
        var engine = ScriptedEngine.Answering("nothing to say");

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiOperations.AnalyzeChangesAsync(
            engine.Runner, engine.Config(), Contexts, string.Empty, "/repo", string.Empty, null,
            run: null, DateTimeOffset.UnixEpoch, Token));

        // The sentence is what `AI-024` documents; the prefix is what tells the renderer to show an
        // empty state rather than an error, and `AiTurn` to file nothing (`XLANG-015`).
        Assert.Equal(
            AiOperations.NothingToAnalyzePrefix + "No hay cambios sin commitear para analizar",
            failure.Message);
    }

    [Fact]
    public async Task An_analysis_payload_is_the_project_context_then_the_diff()
    {
        var engine = ScriptedEngine.Answering("findings");

        await AiOperations.AnalyzeChangesAsync(
            engine.Runner, engine.Config(), Contexts, "@@ -1 +1 @@", "/repo", string.Empty, "/tmp/mcp.json",
            run: null, DateTimeOffset.UnixEpoch, Token);

        Assert.Equal(
            "PROJECT CONTEXT:\n- Conventions: two-space indent\n- Domain: orders are immutable\n\nDIFF:\n@@ -1 +1 @@",
            engine.Only.StdinContent);
        Assert.Equal("/repo", engine.Only.Cwd);
        Assert.Equal("/tmp/mcp.json", engine.Only.McpConfigPath);
    }

    [Fact]
    public async Task An_analysis_with_no_enabled_context_sends_only_the_diff()
    {
        var engine = ScriptedEngine.Answering("findings");

        await AiOperations.AnalyzeChangesAsync(
            engine.Runner, engine.Config(), [], "@@ -1 +1 @@", "/repo", string.Empty, null,
            run: null, DateTimeOffset.UnixEpoch, Token);

        Assert.Equal("DIFF:\n@@ -1 +1 @@", engine.Only.StdinContent);
    }

    [Fact]
    public async Task An_analysis_is_stamped_with_the_model_the_cli_reported_not_the_one_configured()
    {
        // They differ whenever the model setting is blank and the CLI picked its own default.
        var engine = ScriptedEngine.Answering("the findings", model: "claude-opus-4-6");

        var text = await AiOperations.AnalyzeChangesAsync(
            engine.Runner, engine.Config(model: string.Empty), [], "diff", "/repo", string.Empty, null,
            run: null, new DateTimeOffset(2026, 7, 29, 14, 5, 0, TimeSpan.Zero), Token);

        Assert.Equal(
            "the findings\n\n---\n🤖 Análisis automatizado (análisis pre-commit) · Scripted (claude-opus-4-6) · 2026-07-29 14:05",
            text);
    }

    [Fact]
    public async Task An_analysis_with_no_model_anywhere_says_so_in_spanish()
    {
        var engine = ScriptedEngine.Answering("the findings");

        var text = await AiOperations.AnalyzeChangesAsync(
            engine.Runner, engine.Config(model: "  "), [], "diff", "/repo", string.Empty, null,
            run: null, new DateTimeOffset(2026, 7, 29, 14, 5, 0, TimeSpan.Zero), Token);

        Assert.EndsWith("· Scripted (modelo predeterminado) · 2026-07-29 14:05", text, StringComparison.Ordinal);
    }

    // ---------- chat ----------

    [Fact]
    public async Task The_first_turn_establishes_the_context_and_the_next_one_does_not()
    {
        var engine = ScriptedEngine.Answering("an answer", sessionId: "sess-1");

        await AiOperations.ChatAsync(
            engine.Runner, engine.Config(), Contexts, "what does this do?", sessionId: null, "/repo", null,
            run: null, Token);

        Assert.Contains("PROJECT CONTEXT:", engine.Invocations[0].StdinContent, StringComparison.Ordinal);
        Assert.Equal(Prompts.DefaultChatSystemPrompt, engine.Invocations[0].SystemPrompt);

        await AiOperations.ChatAsync(
            engine.Runner, engine.Config(), Contexts, "and this?", sessionId: "sess-1", "/repo", null,
            run: null, Token);

        // A resumed session already carries the earlier turns; re-sending would waste the window and
        // re-establish a system prompt the engine already has.
        Assert.Equal(string.Empty, engine.Invocations[1].StdinContent);
        Assert.Null(engine.Invocations[1].SystemPrompt);
        Assert.Equal("sess-1", engine.Invocations[1].ResumeSessionId);
    }

    [Fact]
    public async Task An_engine_that_cannot_resume_gets_the_context_on_every_turn()
    {
        // AI-044: Ollama holds no conversation state, so there is nothing carrying the context
        // forward and it has to be re-sent even though a session id is in hand.
        var engine = ScriptedEngine.Answering("an answer", resumesSessions: false);

        await AiOperations.ChatAsync(
            engine.Runner, engine.Config(), Contexts, "and this?", sessionId: "sess-1", "/repo", null,
            run: null, Token);

        Assert.Contains("PROJECT CONTEXT:", engine.Only.StdinContent, StringComparison.Ordinal);
        Assert.Equal(Prompts.DefaultChatSystemPrompt, engine.Only.SystemPrompt);
    }

    [Fact]
    public async Task The_chat_carries_the_message_on_the_prompt_and_never_in_the_payload()
    {
        // stdin is data, the prompt is the ask. Folding the message into stdin would leave the CLI
        // with nothing to answer.
        var engine = ScriptedEngine.Answering("an answer");

        await AiOperations.ChatAsync(
            engine.Runner, engine.Config(), [], "why is this slow?", sessionId: null, "/repo", null,
            run: null, Token);

        Assert.Equal("why is this slow?", engine.Only.Prompt);
        Assert.Equal(string.Empty, engine.Only.StdinContent);
    }

    [Fact]
    public async Task The_chat_always_auto_approves_edits()
    {
        // A headless run cannot answer a permission prompt, and the chat is meant to help work on the
        // repository — it gets a checkpoint instead of a dialog.
        var engine = ScriptedEngine.Answering("an answer");

        await AiOperations.ChatAsync(
            engine.Runner, engine.Config(), [], "fix the typo", sessionId: null, "/repo", null, run: null, Token);

        Assert.True(engine.Only.AutoApproveEdits);
    }

    // ---------- fix with AI ----------

    [Fact]
    public async Task A_non_agentic_provider_refuses_to_apply_a_fix_rather_than_pretending_to()
    {
        // The UI hides this for local models, but a local model with no write tools must never
        // silently "fix" nothing if the command is reached another way.
        var engine = ScriptedEngine.Answering("done", agentic: false);

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiOperations.ApplyFindingFixAsync(
            engine.Runner, engine.Config(), "the finding", "/repo", run: null, Token));

        Assert.Equal(
            "Este proveedor local no puede aplicar cambios automáticamente. Usa Claude, Gemini u Open Code para \"Corregir con IA\".",
            failure.Message);
        Assert.Empty(engine.Invocations);
    }

    [Fact]
    public async Task A_fix_runs_on_the_engines_write_tools_and_not_the_users_allow_list()
    {
        // Clicking "fix with AI" is itself the write opt-in, so there is no second setting to get
        // wrong — and the user's general allow-list has no say here.
        var engine = ScriptedEngine.Answering("changed one line");

        await AiOperations.ApplyFindingFixAsync(
            engine.Runner, engine.Config(tools: ["Read"]), "the finding", "/repo", run: null, Token);

        Assert.Equal(["Edit", "Write"], engine.Only.Tools);
        Assert.Equal(Prompts.FixFindingSystemPrompt, engine.Only.SystemPrompt);
        Assert.True(engine.Only.AutoApproveEdits);
    }

    // ---------- inline edit ----------

    [Fact]
    public async Task An_inline_edit_with_nothing_selected_is_refused()
    {
        var engine = ScriptedEngine.Answering("code");

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiOperations.InlineEditAsync(
            engine.Runner, engine.Config(), "a.ts", "the whole file", "  ", "make it faster", run: null, Token));

        Assert.Equal("No hay código seleccionado para editar", failure.Message);
    }

    [Fact]
    public async Task An_inline_edit_sends_the_file_the_selection_and_the_instruction()
    {
        var engine = ScriptedEngine.Answering("const x = 1;");

        await AiOperations.InlineEditAsync(
            engine.Runner, engine.Config(), "src/a.ts", "let x = 1;", "let x = 1;", "prefer const", run: null, Token);

        Assert.Equal(
            """
            ARCHIVO: src/a.ts

            === CONTENIDO DEL ARCHIVO (contexto) ===
            let x = 1;

            === FRAGMENTO SELECCIONADO ===
            let x = 1;

            === INSTRUCCIÓN ===
            prefer const
            """,
            engine.Only.StdinContent);

        // Text in, text out: no tools, no working directory, so it works with every provider.
        Assert.Empty(engine.Only.Tools);
        Assert.Null(engine.Only.Cwd);
    }

    [Theory]
    [InlineData("```ts\nconst x = 1;\n```", "const x = 1;")]
    [InlineData("```\nconst x = 1;\n```", "const x = 1;")]
    [InlineData("const x = 1;", "const x = 1;")]
    [InlineData("  const x = 1;  ", "const x = 1;")]
    [InlineData("```ts\nconst x = 1;", "const x = 1;")]
    [InlineData("```", "")]
    public async Task A_wrapping_code_fence_is_stripped_from_anything_written_to_a_buffer(
        string answer, string expected)
    {
        // Some models fence their answer despite being told not to, and this text is written straight
        // into the editor's buffer.
        var engine = ScriptedEngine.Answering(answer);

        var rewritten = await AiOperations.InlineEditAsync(
            engine.Runner, engine.Config(), "a.ts", "file", "selection", "do it", run: null, Token);

        Assert.Equal(expected, rewritten);
    }

    // ---------- conflict resolution ----------

    [Fact]
    public async Task A_conflict_gets_all_three_sides_labelled_in_spanish()
    {
        var engine = ScriptedEngine.Answering("```\nmerged\n```");

        var resolved = await AiOperations.ResolveConflictAsync(
            engine.Runner, engine.Config(), "src/a.ts", "base text", "our text", "their text",
            string.Empty, run: null, Token);

        Assert.Equal(
            """
            ARCHIVO: src/a.ts

            === BASE (ancestro común) ===
            base text

            === OURS (rama actual) ===
            our text

            === THEIRS (rama entrante) ===
            their text
            """,
            engine.Only.StdinContent);

        Assert.Equal(Prompts.DefaultResolveConflictTemplate, engine.Only.Prompt);
        Assert.Equal("merged", resolved);
    }

    [Fact]
    public async Task A_failing_engine_surfaces_its_message_unchanged()
    {
        var engine = ScriptedEngine.Failing("claude exited with an error (exit status: 1): no such model");

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiOperations.GenerateCommitMessageAsync(
            engine.Runner, engine.Config(), "diff", string.Empty, run: null, Token));

        Assert.Equal("claude exited with an error (exit status: 1): no such model", failure.Message);
    }
}
