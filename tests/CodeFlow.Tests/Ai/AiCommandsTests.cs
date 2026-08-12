using System.Text.Json;
using CodeFlow.Ai;
using CodeFlow.Ai.Engines;
using CodeFlow.Ipc;
using CodeFlow.Tests.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// The AI command surface, and the engine catalogue behind it.
/// See <c>docs/business-rules/05-ai-engines.md</c>.
/// </summary>
public sealed class AiCommandsTests
{
    private static readonly string[] Expected =
    [
        "cancel_ai_run", "list_ai_models", "check_ai_provider",
        "default_commit_template", "default_review_template", "default_analyze_template",
        "default_pr_description_template", "default_resolve_conflict_template",
        "generate_commit_message", "resolve_conflict_with_ai", "inline_edit_with_ai",
        // `analyze_working_changes` is gone: it is one half of `review_changes`
        // (`Tickets/TicketCommands.cs`), which carries the scope and the ticket axes together.
        "resolve_finding_with_ai", "send_chat_message",
    ];

    [Fact]
    public void The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        using var db = new TempDatabase();
        using var http = new HttpClient();

        var registry = new CommandRegistry()
            .AddAiCommands(new AiRunRegistry((_, _, _) => ValueTask.CompletedTask), db.Handle, http);

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_chat_reply_is_spelled_the_way_the_renderer_reads_it()
    {
        // commands.ts:496 declares { text, session_id, model, provider, engine_version, created_at,
        // response_time_ms }. camelCase here compiles and then reads as undefined in the chat bubble.
        var payload = JsonSerializer.SerializeToDocument(
            new AiTurn.ChatReply("t", "s", "m", "p", "v", "c", 12), AiJsonContext.Default.ChatReply);

        Assert.Equal(
            ["text", "session_id", "model", "provider", "engine_version", "created_at", "response_time_ms"],
            payload.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void A_stored_trace_is_spelled_the_way_the_renderer_rehydrates_it()
    {
        // chatStore.ts's parseTrace reads { stream, line } out of the JSON array in activity_log.trace.
        var payload = JsonSerializer.SerializeToDocument(
            (IReadOnlyList<TraceLine>)[new TraceLine("stderr", "probing")],
            AiJsonContext.Default.IReadOnlyListTraceLine);

        var line = Assert.Single(payload.RootElement.EnumerateArray());
        Assert.Equal(["stream", "line"], line.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("gemini", "gemini")]
    [InlineData("opencode", "opencode")]
    [InlineData("ollama", "ollama")]
    [InlineData("openai", "openai")]
    public void Each_known_provider_resolves_to_its_own_engine(string provider, string expectedId) =>
        Assert.Equal(expectedId, EngineCatalog.EngineFor(provider).Id);

    [Theory]
    [InlineData("local")]
    public void Local_is_an_alias_of_ollama(string provider) =>
        Assert.Equal("ollama", EngineCatalog.EngineFor(provider).Id);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-provider-a-future-version-dropped")]
    public void An_unrecognised_provider_falls_back_to_claude(string provider)
    {
        // AI-001. The fallback is what guarantees a corrupt or missing ai_provider setting never
        // leaves the app with no working engine — so it is asserted, not assumed.
        Assert.Equal("claude", EngineCatalog.EngineFor(provider).Id);
    }

    [Fact]
    public void Only_claude_defines_a_dedicated_commit_model()
    {
        foreach (var provider in EngineCatalog.KnownProviders)
        {
            var engine = EngineCatalog.EngineFor(provider);
            var expected = engine.Id == "claude";

            Assert.Equal(expected, engine.CommitMessageModel.Length > 0);
        }
    }

    [Fact]
    public void The_two_http_engines_are_the_non_agentic_ones()
    {
        // Which is why "fix with AI" and MCP are hidden for them: there is no tool loop to run.
        // Typed as the interface on purpose: Agentic is a default interface member, so reading it
        // off the concrete type would not compile — and that is the shape callers actually use.
        foreach (IAiEngine engine in new IAiEngine[] { new Ollama(), new OpenAi("k") })
        {
            Assert.False(engine.Agentic, engine.Id);
        }

        foreach (IAiEngine engine in new IAiEngine[] { new Claude(), new Codex(), new Gemini(), new OpenCode() })
        {
            Assert.True(engine.Agentic, engine.Id);
        }
    }

    [Fact]
    public void An_http_engine_never_offers_a_subprocess_to_run()
    {
        // AI-003: the transport branches first, so these are unreachable. Throwing rather than
        // returning a bogus command is what makes a routing mistake loud instead of spawning
        // something nonsensical.
        var invocation = new AiInvocation("hi", StdinContent: "");

        Assert.Throws<NotSupportedException>(() => new Ollama().BuildCommand("x", invocation));
        Assert.Throws<NotSupportedException>(() => new OpenAi("k").BuildCommand("x", invocation));
        Assert.Throws<NotSupportedException>(() => new Ollama().Interpret(true, "", "", ""));
        Assert.Throws<NotSupportedException>(() => new OpenAi("k").Interpret(true, "", "", ""));
    }

    [Fact]
    public void The_openai_key_rides_on_the_transport_and_never_on_an_invocation()
    {
        // The credential invariant, made structural: there is no invocation field that could carry
        // a key into a subprocess's argv or environment.
        var transport = Assert.IsType<Transport.OpenAiCompatible>(new OpenAi("sk-secret").Transport);
        Assert.Equal("sk-secret", transport.ApiKey);

        Assert.DoesNotContain(
            typeof(AiInvocation).GetProperties(),
            p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_codex_folds_the_whole_brief_into_stdin()
    {
        // AI-004. An engine whose CLI reads stdin is piped just the data; one whose CLI does not is
        // piped nothing at all, because an empty payload is how it says so (AI-054).
        var invocation = new AiInvocation("the ask", StdinContent: "the data", SystemPrompt: "be terse");

        Assert.Equal("the data", ((IAiEngine)new Claude()).StdinPayload(invocation));

        foreach (IAiEngine engine in new IAiEngine[] { new Gemini(), new OpenCode() })
        {
            Assert.Equal(string.Empty, engine.StdinPayload(invocation));
        }

        var codex = new Codex().StdinPayload(invocation);
        Assert.Contains("be terse", codex, StringComparison.Ordinal);
        Assert.Contains("the ask", codex, StringComparison.Ordinal);
        Assert.Contains("----- INPUT -----", codex, StringComparison.Ordinal);
        Assert.Contains("the data", codex, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_built_in_template_has_text()
    {
        // They ship as embedded files rather than literals, so a missing one is a runtime failure
        // in the settings editor — cheap to catch here instead.
        Assert.NotEmpty(Prompts.DefaultCommitTemplate);
        Assert.NotEmpty(Prompts.DefaultReviewPrompt);
        Assert.NotEmpty(Prompts.DefaultAnalyzeTemplate);
        Assert.NotEmpty(Prompts.DefaultPrDescriptionTemplate);
        Assert.NotEmpty(Prompts.DefaultResolveConflictTemplate);
        Assert.NotEmpty(Prompts.DefaultPrReviewStandard);
        Assert.NotEmpty(Prompts.DefaultChatSystemPrompt);
        Assert.NotEmpty(Prompts.FixFindingSystemPrompt);
        Assert.NotEmpty(Prompts.DefaultInlineEditPrompt);
    }

    [Fact]
    public void The_prompts_extracted_from_rust_literals_carry_no_stray_indentation()
    {
        // CodeFlow 1.7.2 wrote these as string literals with line continuations, which
        // swallow the newline *and* the next line's leading whitespace. A copy-paste transcription
        // would silently indent two thirds of the lines, which changes the text the model reads.
        foreach (var prompt in new[]
                 {
                     Prompts.DefaultChatSystemPrompt,
                     Prompts.FixFindingSystemPrompt,
                     Prompts.DefaultInlineEditPrompt,
                     Prompts.DefaultPrDescriptionTemplate,
                 })
        {
            Assert.DoesNotContain(prompt.Split('\n'), line => line.StartsWith(' ') || line.StartsWith('\t'));
            Assert.False(prompt.EndsWith('\n'), "the prompt files carry no trailing newline");
        }
    }
}
