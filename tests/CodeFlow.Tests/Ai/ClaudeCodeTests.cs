using System.Text.Json;
using CodeFlow.Ai;
using CodeFlow.Ai.Engines;
using CodeFlow.Tests.TestVectors;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// Claude Code output interpretation, driven by the vectors The extraction pass extracted from the extracted cases.
/// </summary>
/// <remarks>
/// Every case here came from a real case extracted from 1.7.2, so these are not a new
/// opinion about how the CLI behaves — they are 1.7.2's own assertions, replayed.
/// </remarks>
public sealed class ClaudeCodeTests
{
    private static readonly Claude Engine = new();

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, "claude.vectors.json")))
        {
            foreach (var testCase in fixture.Cases)
            {
                data.Add(testCase.Id!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Interpret_matches_the_extracted_vector(string caseId)
    {
        var testCase = Load(caseId);

        var success = testCase.Input.GetProperty("success").GetBoolean();
        var stdout = testCase.Input.GetProperty("stdout").GetString() ?? string.Empty;
        var stderr = testCase.Input.GetProperty("stderr").GetString() ?? string.Empty;

        var statusLabel = testCase.Input.TryGetProperty("statusLabel", out var label)
            ? label.GetString() ?? string.Empty
            : string.Empty;

        if (testCase.Expected.TryGetProperty("error", out var expectedError))
        {
            var thrown = Assert.Throws<AiRunFailedException>(() => Engine.Interpret(success, statusLabel, stdout, stderr));
            Assert.Equal(expectedError.GetString(), thrown.Message);
            return;
        }

        var run = Engine.Interpret(success, statusLabel, stdout, stderr);
        Assert.Equal(Text(testCase.Expected, "text"), run.Text);
        Assert.Equal(Text(testCase.Expected, "sessionId"), run.SessionId);
        Assert.Equal(Text(testCase.Expected, "model"), run.Model);
    }

    [Fact]
    public void The_invocation_carries_the_flags_stream_json_requires()
    {
        var info = Engine.BuildCommand("claude", new AiInvocation("hello", StdinContent: "", Model: "claude-opus-5"));
        var args = info.ArgumentList;

        Assert.Equal("claude", info.FileName);
        Assert.Equal(
            [
                "-p", "hello",
                "--model", "claude-opus-5",
                "--output-format", "stream-json",
                "--verbose",
                "--setting-sources", "user",
            ],
            args);

        // --verbose is not optional decoration: the CLI rejects stream-json without it in -p mode,
        // and without stream-json there is nothing to show in the activity log until the process
        // exits.
        Assert.Contains("--verbose", args);
    }

    [Fact]
    public void A_tool_list_bounds_what_exists_and_not_only_what_is_approved()
    {
        // `--allowedTools` alone was the wrong flag and changed nothing: it says which tools run
        // without asking, not which exist. Measured after shipping it, the agent went on calling
        // `Bash` seventeen times in one review. `--tools` is the one that bounds the set.
        var info = Engine.BuildCommand(
            "claude", new AiInvocation("hello", StdinContent: "", AllowedTools: ["Read", "Grep", "Glob"]));

        var tools = info.ArgumentList.IndexOf("--tools");
        Assert.True(tools >= 0, "the run could still reach for any built-in tool");
        Assert.Equal("Read,Grep,Glob", info.ArgumentList[tools + 1]);

        // Auto-approved as well, because a headless run has nobody to ask.
        var allowed = info.ArgumentList.IndexOf("--allowedTools");
        Assert.True(allowed >= 0);
        Assert.Equal("Read,Grep,Glob", info.ArgumentList[allowed + 1]);

        Assert.DoesNotContain("Bash", info.ArgumentList);
    }

    [Fact]
    public void No_tool_list_leaves_the_engines_own_defaults_alone()
    {
        var info = Engine.BuildCommand("claude", new AiInvocation("hello", StdinContent: ""));

        Assert.DoesNotContain("--tools", info.ArgumentList);
        Assert.DoesNotContain("--allowedTools", info.ArgumentList);
    }

    [Fact]
    public void An_empty_tool_list_is_an_answer_rather_than_the_absence_of_one()
    {
        // `--tools ""` is the CLI's own spelling of "disable all tools", and this is the difference
        // between a review that was already handed the code around every change and one that was
        // merely never configured. Treated as the same thing, the first silently became the second
        // and the run got every tool the CLI offers.
        var info = Engine.BuildCommand("claude", new AiInvocation("hello", StdinContent: "", AllowedTools: []));

        var tools = info.ArgumentList.IndexOf("--tools");
        Assert.True(tools >= 0, "the run would fall back to the CLI's own defaults");
        Assert.Equal(string.Empty, info.ArgumentList[tools + 1]);

        // Nothing to auto-approve when nothing can run.
        Assert.DoesNotContain("--allowedTools", info.ArgumentList);
    }

    [Fact]
    public void A_run_never_loads_the_analysed_repositorys_own_settings()
    {
        // The CLI's working directory is the repository being reviewed, so without this it runs
        // that repository's hooks inside CodeFlow's own analysis — a `Stop` hook that type-checks
        // and lints held one review open for five minutes. `user` and nothing else: `project` is
        // the repository's `.claude/settings.json` and `local` is the same repository's
        // `settings.local.json`.
        var info = Engine.BuildCommand("claude", new AiInvocation("hello", StdinContent: "", Cwd: "/tmp/repo"));

        var sources = info.ArgumentList.IndexOf("--setting-sources");
        Assert.True(sources >= 0, "the run would inherit the reviewed repository's settings");
        Assert.Equal("user", info.ArgumentList[sources + 1]);
    }

    /// <summary>
    /// A real <c>result</c> event, trimmed to the fields this reads.
    /// </summary>
    /// <remarks>
    /// Captured from <c>claude -p … --output-format stream-json --verbose</c> rather than written
    /// from memory: the names are the contract, and <c>cache_creation_input_tokens</c> against
    /// <c>cacheCreationInputTokens</c> — both of which the CLI emits, in different objects — is the
    /// kind of difference that costs an afternoon.
    /// </remarks>
    private const string RealResultEvent =
        """
        {"type":"result","subtype":"success","is_error":false,"result":"pong",
         "session_id":"03bc2b7f-cbea-4893-8599-b66e998d2e31","total_cost_usd":0.0157075,
         "duration_ms":2239,"num_turns":1,
         "usage":{"input_tokens":2,"cache_creation_input_tokens":11,"cache_read_input_tokens":26635,
                  "output_tokens":4,"service_tier":"standard"},
         "modelUsage":{"claude-sonnet-5":{"inputTokens":695,"outputTokens":13}}}
        """;

    [Fact]
    public void A_finished_run_reports_what_it_consumed()
    {
        // None of this was kept before: two reviews, one twice as slow as the other, were
        // indistinguishable in cost, and answering "did that get cheaper?" meant reading the CLI's
        // own session files by hand.
        var run = Engine.Interpret(success: true, "exit status: 0", RealResultEvent, string.Empty);

        Assert.NotNull(run.Usage);
        var usage = run.Usage!;
        Assert.Equal(2, usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(26_635, usage.CacheReadTokens);
        Assert.Equal(11, usage.CacheWriteTokens);
        Assert.Equal(0.0157075, usage.CostUsd);
        Assert.Equal(2_239, usage.DurationMs);
    }

    [Fact]
    public void An_engine_that_reports_nothing_reports_null_rather_than_zero()
    {
        // A zero would read as a free run instead of an unmeasured one, and the difference matters
        // for the five engines that never report usage at all.
        var run = Engine.Interpret(
            success: true, "exit status: 0", """{"type":"result","is_error":false,"result":"pong"}""", string.Empty);

        Assert.Null(run.Usage);
        Assert.Equal("pong", run.Text);
    }

    [Fact]
    public void Usage_survives_a_result_that_carries_no_cost()
    {
        // `total_cost_usd` is the engine's own figure and is not always present. Its absence must
        // not take the token counts with it — and nothing here recalculates a price locally.
        var run = Engine.Interpret(
            success: true,
            "exit status: 0",
            """{"type":"result","is_error":false,"result":"ok","usage":{"input_tokens":7,"output_tokens":3}}""",
            string.Empty);

        Assert.NotNull(run.Usage);
        var usage = run.Usage!;
        Assert.Equal(7, usage.InputTokens);
        Assert.Null(usage.CostUsd);
        Assert.Null(usage.DurationMs);
    }

    [Fact]
    public void Quota_refusals_are_tagged_for_the_frontend()
    {
        // The marker is a cross-language contract: src/lib/claudeError.ts splits what follows it
        // into a usage-versus-billing case. An untagged refusal renders as a generic red error.
        Assert.Equal("QUOTA_EXCEEDED::", QuotaSignals.Marker);
        Assert.True(QuotaSignals.Matches("You have hit your usage limit"));
        Assert.True(QuotaSignals.Matches("Insufficient balance"));
        Assert.False(QuotaSignals.Matches("connection reset by peer"));

        // Already-tagged messages are not tagged twice.
        Assert.Equal("QUOTA_EXCEEDED::rate limit", QuotaSignals.Mark("QUOTA_EXCEEDED::rate limit"));
    }

    private static FixtureCase Load(string caseId) =>
        FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, "claude.vectors.json"))
            .SelectMany(f => f.Cases)
            .Single(c => c.Id == caseId);

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
