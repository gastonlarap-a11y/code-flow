using CodeFlow.Ai;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// The review operation's prompt, payload and depth directive.
/// See <c>docs/business-rules/05-ai-engines.md</c> <c>AI-022</c> and <c>AI-023</c>.
/// </summary>
public sealed class ReviewOperationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("basico", "básico")]
    [InlineData("básico", "básico")]
    [InlineData("completo", "completo")]
    [InlineData("ultra", "ultra")]
    // Anything unrecognised, including blank, is completo — never an error. That is what lets the
    // renderer's level selector gain an option without a backend change.
    [InlineData("", "completo")]
    [InlineData("BASICO", "completo")]
    [InlineData("no-such-level", "completo")]
    public async Task The_level_directive_rides_at_the_end_of_the_prompt(string level, string expected)
    {
        var captured = await CaptureAsync(level: level);

        Assert.EndsWith($"## NIVEL DE REVISIÓN ACTIVO: {expected}", Head(captured.Prompt), StringComparison.Ordinal);
        // After the methodology, so it overrides whatever depth that implies rather than being
        // overridden by it.
        Assert.Contains("\n\n## NIVEL DE REVISIÓN ACTIVO:", captured.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_three_directives_say_different_things_about_confidence()
    {
        Assert.Contains("confidence ≥ 75", (await CaptureAsync(level: "basico")).Prompt, StringComparison.Ordinal);
        Assert.Contains("confidence ≥ 60", (await CaptureAsync(level: "completo")).Prompt, StringComparison.Ordinal);
        Assert.Contains("confidence ≥ 50", (await CaptureAsync(level: "ultra")).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ultra_only_sends_a_run_looking_for_code_when_there_is_code_to_look_at()
    {
        // The contradiction this closes reached the model down two channels of one invocation: the
        // level directive on argv told it to read the whole method around every change, while the
        // no-clone context on stdin told it not to try — in a working directory holding a
        // description and a diff.
        var cloned = await CaptureAsync(level: "ultra", explorable: true);
        var linked = await CaptureAsync(level: "ultra", explorable: false);

        Assert.Contains("CODE AROUND THE CHANGES", cloned.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("CODE AROUND THE CHANGES", linked.Prompt, StringComparison.Ordinal);
        Assert.Contains("no checkout", linked.Prompt, StringComparison.Ordinal);

        // Both are still ultra: the depth is unchanged, only the instruction it cannot follow is.
        Assert.Contains("## NIVEL DE REVISIÓN ACTIVO: ultra", linked.Prompt, StringComparison.Ordinal);
        Assert.Contains("confidence ≥ 50", linked.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_extracted_code_rides_after_the_diff()
    {
        // After, because the diff is what the review is of and this is the code it lands in — and
        // because it is the half a link review has no way to build.
        var captured = await CaptureAsync(codeContext: "CODE AROUND THE CHANGES\n── a lines 1-2\n");

        Assert.Equal(
            "PR TITLE: Add the thing\n\nPR DESCRIPTION:\nthe description\n\nDIFF:\ndiff --git a/a b/a\n\n"
            + "CODE AROUND THE CHANGES\n── a lines 1-2\n",
            captured.StdinContent);
    }

    [Fact]
    public async Task The_payload_names_the_pull_request_before_the_diff()
    {
        var captured = await CaptureAsync();

        Assert.Equal(
            "PR TITLE: Add the thing\n\nPR DESCRIPTION:\nthe description\n\nDIFF:\ndiff --git a/a b/a\n",
            captured.StdinContent);
    }

    [Fact]
    public async Task A_blank_description_reads_as_no_description()
    {
        var captured = await CaptureAsync(description: "   ");

        Assert.Contains("PR DESCRIPTION:\n(no description)\n", captured.StdinContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_contexts_ride_under_their_own_heading()
    {
        var captured = await CaptureAsync(contexts: [("Convenciones", "usa camelCase"), ("Seguridad", "sin secretos")]);

        Assert.Contains(
            "PROJECT REVIEW CONTEXT:\n- Convenciones: usa camelCase\n- Seguridad: sin secretos\n\nDIFF:",
            captured.StdinContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_request_with_nothing_in_it_is_refused_before_the_engine_runs()
    {
        var ran = false;

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiOperations.ReviewPullRequestAsync(
            (_, _, _, _) =>
            {
                ran = true;
                return Task.FromResult(new AiRun("", null, null));
            },
            Config(), "t", "d", [], "   ", "", "/tmp", "", "completo", true, null, null, Ct));

        Assert.Equal("This pull request has no changes to review", failure.Message);
        Assert.False(ran);
    }

    [Fact]
    public async Task A_workspace_template_replaces_the_built_in_methodology()
    {
        var captured = await CaptureAsync(template: "Revisa solo la seguridad.");

        Assert.StartsWith("Revisa solo la seguridad.\n\n", captured.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_template_falls_back_to_the_built_in_one()
    {
        var captured = await CaptureAsync(template: "   ");

        Assert.StartsWith(Prompts.DefaultReviewPrompt, captured.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_run_comes_back_whole_for_the_pipeline_to_stamp()
    {
        // Unstamped on purpose. Half of what belongs in that footer — how long the whole operation
        // took, how much of the change reached the model, what the findings did since the last run —
        // is known in `ReviewRun` and not here, and the footer has to be the last thing in the text.
        var run = await AiOperations.ReviewPullRequestAsync(
            (_, _, _, _) => Task.FromResult(new AiRun("cuerpo", null, "claude-opus-5")),
            Config(), "t", "d", [], "diff", "", "/tmp", "", "completo", true, null, null, Ct);

        Assert.Equal("cuerpo", run.Text);
        // What the CLI reported it ran beats what was configured.
        Assert.Equal("claude-opus-5", run.Model);
    }

    /// <summary>Runs the operation against a runner that records what it was handed.</summary>
    private static async Task<AiInvocation> CaptureAsync(
        string level = "completo",
        string description = "the description",
        string template = "",
        string codeContext = "",
        bool explorable = true,
        IReadOnlyList<(string Name, string Content)>? contexts = null)
    {
        AiInvocation? captured = null;

        await AiOperations.ReviewPullRequestAsync(
            (_, invocation, _, _) =>
            {
                captured = invocation;
                return Task.FromResult(new AiRun("cuerpo", null, null));
            },
            Config(), "Add the thing", description, contexts ?? [], "diff --git a/a b/a\n", codeContext, "/tmp",
            template, level, explorable, null, null, Ct);

        Assert.NotNull(captured);
        return captured;
    }

    /// <summary>The directive's first line, which is what names the level.</summary>
    private static string Head(string prompt) => prompt.Split('\n')[^2..][0];

    /// <summary>A routing result, built directly — this file is about the operation, not the cascade.</summary>
    private static AiConfig Config() => new(
        EngineCatalog.EngineFor("claude"), "claude", "claude-opus-5", "claude", []);
}
