using System.Text.Json;
using CodeFlow.Ai;
using CodeFlow.Ai.Engines;
using CodeFlow.Tests.TestVectors;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// Every engine's output interpretation, driven by the vectors The extraction pass extracted from the
/// the extracted cases.
/// </summary>
/// <remarks>
/// These are not a fresh opinion about how each CLI behaves — they are 1.7.2's assertions,
/// replayed against this codebase. That is the only way to tell a faithful translation from a plausible
/// one here, because most of these paths cannot be exercised without the real CLI and a real
/// account.
/// </remarks>
public sealed class EngineVectorTests
{
    /// <summary>Which fixture file speaks for which engine.</summary>
    private static readonly Dictionary<string, Func<IAiEngine>> Engines = new(StringComparer.Ordinal)
    {
        ["codex.vectors.json"] = () => new Codex(),
        ["gemini.vectors.json"] = () => new Gemini(),
        ["opencode.vectors.json"] = () => new OpenCode(),
    };

    public static TheoryData<string, string> InterpretCases() =>
        CasesFor("interpret_output", [.. Engines.Keys]);

    [Theory]
    [MemberData(nameof(InterpretCases))]
    public void Interpret_matches_the_extracted_vector(string file, string caseId)
    {
        var engine = Engines[file]();
        var testCase = Find(file, caseId);

        var success = testCase.Input.GetProperty("success").GetBoolean();
        var statusLabel = Str(testCase.Input, "statusLabel") ?? string.Empty;
        var stdout = Str(testCase.Input, "stdout") ?? string.Empty;
        var stderr = Str(testCase.Input, "stderr") ?? string.Empty;

        if (Str(testCase.Expected, "error") is { } expected)
        {
            var thrown = Assert.Throws<AiRunFailedException>(
                () => engine.Interpret(success, statusLabel, stdout, stderr));
            Assert.Equal(expected, thrown.Message);
            return;
        }

        var run = engine.Interpret(success, statusLabel, stdout, stderr);
        Assert.Equal(Str(testCase.Expected, "text"), run.Text);

        // Several cases assert only the text; only check the session when the vector names it.
        if (testCase.Expected.TryGetProperty("sessionId", out _))
        {
            Assert.Equal(Str(testCase.Expected, "sessionId"), run.SessionId);
        }
    }

    public static TheoryData<string, string> CodexSessionCases() =>
        CasesFor("session_id_from_preamble", ["codex.vectors.json"]);

    [Theory]
    [MemberData(nameof(CodexSessionCases))]
    public void The_rollout_id_is_scraped_out_of_the_preamble(string file, string caseId)
    {
        var testCase = Find(file, caseId);

        Assert.Equal(
            Str(testCase.Expected, "result"),
            Codex.SessionIdFromPreamble(testCase.Input.GetProperty("stderr").GetString()!));
    }

    public static TheoryData<string, string> CodexCacheCases() =>
        CasesFor("read_models_cache", ["codex.vectors.json"]);

    [Theory]
    [MemberData(nameof(CodexCacheCases))]
    public void The_model_cache_keeps_listed_entries_in_priority_order(string file, string caseId)
    {
        var testCase = Find(file, caseId);

        using var home = new TempDir();
        var raw = testCase.Input.GetProperty("modelsCacheJson");
        if (raw.ValueKind == JsonValueKind.String)
        {
            File.WriteAllText(Path.Combine(home.Path, "models_cache.json"), raw.GetString()!);
        }

        var expected = testCase.Expected.GetProperty("result") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(m => m.GetString()!).ToArray()
            : null;

        Assert.Equal(expected, Codex.ReadModelsCache(home.Path));
    }

    [Fact]
    public void A_small_brief_stays_on_argv()
    {
        // Gemini's one non-interpret vector: below the inline limit nothing is written to disk, so
        // there is no --add-dir and no leaked temp file.
        Assert.Null(Gemini.WriteBriefFileIfLarge("hola"));
    }

    /// <summary>
    /// The two brief-composing engines carry the input inside the brief, and must not also be
    /// handed it on stdin.
    /// </summary>
    /// <remarks>
    /// They never read stdin, so a payload written there went nowhere twice over: sent a second
    /// time, and into a pipe that then broke on every single run. Normalising that breakage is what
    /// hid a real one from the engines that <em>do</em> read stdin (<c>AI-048</c>).
    /// </remarks>
    [Fact]
    public void A_brief_composing_engine_is_handed_nothing_on_stdin()
    {
        var invocation = new AiInvocation("review this", "DIFF:\n+ the change under review");

        Assert.Equal(string.Empty, new Gemini().StdinPayload(invocation));
        Assert.Equal(string.Empty, new OpenCode().StdinPayload(invocation));

        // And the input is still delivered — inside the brief, which is the whole point.
        Assert.Contains("the change under review", Gemini.ComposeBrief(invocation), StringComparison.Ordinal);

        var command = new OpenCode().BuildCommand("opencode", invocation);
        var scratch = EngineScratch.CollectFrom(command);
        try
        {
            Assert.Contains(
                "the change under review",
                File.ReadAllText(Assert.Single(scratch)),
                StringComparison.Ordinal);
        }
        finally
        {
            EngineScratch.TryDelete(scratch);
        }
    }

    public static TheoryData<string, string> OpenAiErrorCases() =>
        CasesFor("error_detail", ["openai.vectors.json"]);

    [Theory]
    [MemberData(nameof(OpenAiErrorCases))]
    public void The_reason_is_pulled_out_of_an_error_body(string file, string caseId)
    {
        var testCase = Find(file, caseId);

        Assert.Equal(
            Str(testCase.Expected, "result"),
            OpenAi.ErrorDetail(testCase.Input.GetProperty("body").GetString()!));
    }

    public static TheoryData<string, string> ChatModelCases() =>
        CasesFor("is_chat_model", ["openai.vectors.json"]);

    [Theory]
    [MemberData(nameof(ChatModelCases))]
    public void Non_chat_model_families_are_filtered_out_of_the_picker(string file, string caseId)
    {
        var testCase = Find(file, caseId);

        Assert.Equal(
            testCase.Expected.GetProperty("result").GetBoolean(),
            OpenAi.IsChatModel(testCase.Input.GetProperty("id").GetString()!));
    }

    public static TheoryData<string, string> StripAnsiCases() =>
        CasesFor("strip_ansi", ["ai.vectors.json"]);

    [Theory]
    [MemberData(nameof(StripAnsiCases))]
    public void Ansi_escapes_are_stripped_before_an_engine_sees_the_output(string file, string caseId)
    {
        var testCase = Find(file, caseId);

        Assert.Equal(
            Str(testCase.Expected, "result"),
            AiText.StripAnsi(testCase.Input.GetProperty("text").GetString()!));
    }

    public static TheoryData<string, string> VersionCases() =>
        CasesFor("parse_version", ["ai.vectors.json"]);

    [Theory]
    [MemberData(nameof(VersionCases))]
    public void A_version_is_read_out_of_whatever_banner_the_cli_printed(string file, string caseId)
    {
        var testCase = Find(file, caseId);

        Assert.Equal(
            Str(testCase.Expected, "result"),
            AiText.ParseVersion(testCase.Input.GetProperty("output").GetString()!));
    }

    public static TheoryData<string, string> QuotaCases() =>
        CasesFor("quota_signal", ["ai.vectors.json"]);

    [Theory]
    [MemberData(nameof(QuotaCases))]
    public void A_refusal_is_recognised_as_a_quota_signal(string file, string caseId)
    {
        var testCase = Find(file, caseId);

        Assert.Equal(
            testCase.Expected.GetProperty("result").GetBoolean(),
            QuotaSignals.Matches(testCase.Input.GetProperty("text").GetString()!));
    }

    public static TheoryData<string, string> AuthCases() =>
        CasesFor("auth_signal", ["ai.vectors.json"]);

    /// <summary>
    /// The dictionary alone, which is knowingly too broad.
    /// </summary>
    /// <remarks>
    /// One of its cases asserts <see langword="true"/> for an ordinary review finding about a 401,
    /// because that is the truth about the dictionary and pretending otherwise would hide what keeps
    /// the feature safe: <see cref="AuthSignals"/> is only ever consulted on a failure path. The
    /// case that proves <em>that</em> is an engine vector, not this one —
    /// <c>an-ordinary-reply-mentioning-401-is-still-a-reply</c> in <c>codex.vectors.json</c>.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AuthCases))]
    public void A_lost_login_is_recognised_as_an_auth_signal(string file, string caseId)
    {
        var testCase = Find(file, caseId);

        Assert.Equal(
            testCase.Expected.GetProperty("result").GetBoolean(),
            AuthSignals.Matches(testCase.Input.GetProperty("text").GetString()!));
    }

    // -----------------------------------------------------------------------

    /// <summary>Every case of one unit, across the named fixture files.</summary>
    /// <remarks>
    /// Selected by the fixture's own <c>unit</c> label rather than by guessing from the input's
    /// shape: one file holds several units, and a shape-sniffing selector silently runs the wrong
    /// assertion when two of them happen to share a field name.
    /// </remarks>
    private static TheoryData<string, string> CasesFor(string unit, string[] files)
    {
        var data = new TheoryData<string, string>();
        foreach (var file in files)
        {
            foreach (var fixture in FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, file)))
            {
                if (fixture.Unit?.StartsWith(unit, StringComparison.Ordinal) != true)
                {
                    continue;
                }

                foreach (var testCase in fixture.Cases)
                {
                    data.Add(file, testCase.Id!);
                }
            }
        }

        Assert.NotEmpty(data);
        return data;
    }

    private static FixtureCase Find(string file, string caseId) =>
        FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, file))
            .SelectMany(f => f.Cases)
            .Single(c => c.Id == caseId);

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cf-codex-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
