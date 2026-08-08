using CodeFlow.Ai;
using CodeFlow.Ai.Engines;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// The per-task provider and model cascade.
/// See <c>docs/business-rules/13-cross-language-contracts.md</c> <c>XLANG-004</c>, <c>XLANG-005</c>.
/// </summary>
public sealed class AiRoutingTests
{
    /// <summary>
    /// The eight task keys, spelled out rather than read from the code under test.
    /// </summary>
    /// <remarks>
    /// A test that asserted <c>AiRouting.Tasks</c> against itself would pass through any rename.
    /// These strings are duplicated in <c>src/lib/aiTasks.ts</c> and are the settings namespace, so
    /// a rename orphans a user's stored routing silently — this is the check that makes it loud.
    /// </remarks>
    [Fact]
    public void The_eight_task_keys_are_verbatim() =>
        Assert.Equal(
            ["chat", "commit", "analyze", "review", "pr_description", "fix", "conflict", "inline"],
            AiRouting.Tasks);

    [Fact]
    public void With_nothing_configured_every_task_routes_to_claude()
    {
        using var db = new TempDatabase();

        foreach (var task in AiRouting.Tasks)
        {
            Assert.Equal(EngineCatalog.FallbackProvider, db.Use(c => AiRouting.ProviderFor(c, task)));
        }
    }

    [Fact]
    public void A_per_task_provider_wins_over_the_global_one()
    {
        using var db = new TempDatabase();

        db.Do(c => Settings.SetSetting(c, "ai_provider", "codex"));
        db.Do(c => Settings.SetSetting(c, "ai_provider_review", "gemini"));

        Assert.Equal("gemini", db.Use(c => AiRouting.ProviderFor(c, "review")));
        Assert.Equal("codex", db.Use(c => AiRouting.ProviderFor(c, "chat")));
    }

    [Fact]
    public void A_blank_stored_value_counts_as_unset_at_every_step()
    {
        using var db = new TempDatabase();

        // The settings UI clears a row by writing an empty string rather than deleting it, so
        // "stored but blank" is the normal shape of "not configured".
        db.Do(c => Settings.SetSetting(c, "ai_provider", "codex"));
        db.Do(c => Settings.SetSetting(c, "ai_provider_review", "   "));

        Assert.Equal("codex", db.Use(c => AiRouting.ProviderFor(c, "review")));

        db.Do(c => Settings.SetSetting(c, "ai_provider", string.Empty));
        Assert.Equal(EngineCatalog.FallbackProvider, db.Use(c => AiRouting.ProviderFor(c, "review")));
    }

    [Fact]
    public void A_per_task_model_wins_over_the_providers_base_model()
    {
        using var db = new TempDatabase();

        db.Do(c => Settings.SetSetting(c, "claude_model", "base"));
        db.Do(c => Settings.SetSetting(c, "claude_review_model", "per-task"));

        Assert.Equal("per-task", db.Use(c => AiRouting.ModelFor(c, EngineCatalog.FallbackProvider, "review")));
        Assert.Equal("base", db.Use(c => AiRouting.ModelFor(c, EngineCatalog.FallbackProvider, "chat")));
    }

    [Fact]
    public void The_commit_task_falls_to_the_engines_own_commit_model_before_the_base_model()
    {
        using var db = new TempDatabase();

        db.Do(c => Settings.SetSetting(c, "claude_model", "base"));

        // Step 2 of XLANG-005's table, and the half the renderer's own preview omits — that
        // divergence is BUG-XLANG-a, reproduced on the backend side exactly as documented.
        Assert.Equal(new Claude().CommitMessageModel, db.Use(c => AiRouting.ModelFor(c, EngineCatalog.FallbackProvider, "commit")));
        Assert.Equal("base", db.Use(c => AiRouting.ModelFor(c, EngineCatalog.FallbackProvider, "analyze")));
    }

    [Fact]
    public void A_per_task_commit_model_still_wins_over_the_engines_own()
    {
        using var db = new TempDatabase();

        db.Do(c => Settings.SetSetting(c, "claude_commit_model", "chosen"));

        Assert.Equal("chosen", db.Use(c => AiRouting.ModelFor(c, EngineCatalog.FallbackProvider, "commit")));
    }

    [Fact]
    public void A_provider_with_no_commit_model_of_its_own_falls_straight_to_its_base_model()
    {
        using var db = new TempDatabase();

        db.Do(c => Settings.SetSetting(c, "codex_model", "base"));

        // Only Claude defines a commit-message model; the others define it empty, so the step is
        // skipped rather than yielding a blank.
        Assert.Equal("base", db.Use(c => AiRouting.ModelFor(c, "codex", "commit")));
    }

    [Fact]
    public void An_unconfigured_model_is_the_empty_string()
    {
        using var db = new TempDatabase();

        // Which the engines read as "let the CLI choose" — not an error, and not a hardcoded name.
        Assert.Equal(string.Empty, db.Use(c => AiRouting.ModelFor(c, "codex", "chat")));
    }

    [Fact]
    public void The_binary_path_setting_wins_over_the_engines_default()
    {
        using var db = new TempDatabase();

        Assert.Equal(new Claude().DefaultBinary, db.Use(c => AiRouting.Resolve(c, "chat")).BinaryPath);

        db.Do(c => Settings.SetSetting(c, "claude_binary_path", "/opt/homebrew/bin/claude"));
        Assert.Equal("/opt/homebrew/bin/claude", db.Use(c => AiRouting.Resolve(c, "chat")).BinaryPath);
    }

    [Fact]
    public void Allowed_tools_split_on_commas_and_drop_blanks()
    {
        using var db = new TempDatabase();

        db.Do(c => Settings.SetSetting(c, "claude_allowed_tools", " Read , ,Write,"));

        Assert.Equal(["Read", "Write"], db.Use(c => AiRouting.Resolve(c, "chat")).AllowedTools);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("analyze")]
    public void A_judging_task_with_no_setting_falls_back_to_the_recommended_three(string task)
    {
        // An unset list meant no tool flags at all, which handed the decision to whatever the CLI
        // defaults to. Measured on real reviews of this repository, that meant the agent shelling
        // out eleven and seventeen times and reading over two million cached tokens to judge a diff
        // it had already been given. Reading the code is the job; running commands to do it is the
        // expensive one, so `Bash` is not in the fallback.
        using var db = new TempDatabase();

        Assert.Equal(["Read", "Grep", "Glob"], db.Use(c => AiRouting.Resolve(c, task)).AllowedTools);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("fix")]
    public void A_task_that_is_not_judging_is_left_alone(string task)
    {
        // A chat turn is a conversation with the repository and a fix is an edit to it. Bounding
        // either would be taking away something the user asked for; only a run that was handed the
        // change it is meant to judge has no business re-deriving it with shell commands.
        //
        // Null, not empty: the engines now read an empty list as "no tools at all" and send
        // `--tools ""`. Leaving the engine's own defaults alone is a different answer, and this is
        // the one that must not change into the other.
        using var db = new TempDatabase();

        Assert.Null(db.Use(c => AiRouting.Resolve(c, task)).AllowedTools);
    }

    [Fact]
    public void A_list_the_user_cleared_stays_cleared()
    {
        // Blank is a choice, not an absence: clearing every checkbox is how the settings screen says
        // "no tools", and answering that with the fallback would overrule it. It now reaches the CLI
        // as one too — `--tools ""` — where before it merely sent no flag and let the CLI decide.
        using var db = new TempDatabase();
        db.Do(c => Settings.SetSetting(c, "claude_allowed_tools", string.Empty));

        var tools = db.Use(c => AiRouting.Resolve(c, "review")).AllowedTools;

        Assert.NotNull(tools);
        Assert.Empty(tools);
    }

    [Fact]
    public void An_agent_driving_a_judging_task_is_bounded_like_everyone_else()
    {
        // The gap a review of this repository found in its own diff: a workspace agent routes
        // through `ResolveFor`, which knew the provider and the model but not the task — so the one
        // run configured to be exhaustive was also the one running with every tool the CLI offers,
        // while the ordinary route was held to three.
        using var db = new TempDatabase();

        var config = db.Use(c => AiRouting.ResolveFor(c, EngineCatalog.FallbackProvider, "agent-model", "review"));

        Assert.Equal(["Read", "Grep", "Glob"], config.AllowedTools);
    }

    [Fact]
    public void Resolving_for_an_agent_takes_its_model_verbatim_and_ignores_task_routing()
    {
        using var db = new TempDatabase();

        db.Do(c => Settings.SetSetting(c, "ai_provider_chat", "gemini"));
        db.Do(c => Settings.SetSetting(c, "claude_chat_model", "would-be-chosen"));
        db.Do(c => Settings.SetSetting(c, "claude_allowed_tools", "Read"));

        var config = db.Use(c => AiRouting.ResolveFor(c, EngineCatalog.FallbackProvider, "agent-model", "chat"));

        // An active agent replaces steps 1 and 2 of the cascade outright rather than overriding
        // its result: the per-task keys are never consulted, but the provider's binary and tool
        // settings still apply.
        Assert.Equal(EngineCatalog.FallbackProvider, config.Provider);
        Assert.Equal("agent-model", config.Model);
        Assert.Equal(["Read"], config.AllowedTools);
    }

    [Fact]
    public void A_shared_template_prefers_the_current_key_and_falls_back_to_the_legacy_one()
    {
        using var db = new TempDatabase();

        Assert.Equal(string.Empty, db.Use(c => AiRouting.SharedTemplate(c, "commit_template")));

        db.Do(c => Settings.SetSetting(c, "claude_commit_template", "legacy"));
        Assert.Equal("legacy", db.Use(c => AiRouting.SharedTemplate(c, "commit_template")));

        db.Do(c => Settings.SetSetting(c, "commit_template", "current"));
        Assert.Equal("current", db.Use(c => AiRouting.SharedTemplate(c, "commit_template")));

        // Blank, not just absent, falls through — a renamed settings key has no migration path,
        // and this fallback is what stops one from stranding whatever the user wrote.
        db.Do(c => Settings.SetSetting(c, "commit_template", "  "));
        Assert.Equal("legacy", db.Use(c => AiRouting.SharedTemplate(c, "commit_template")));
    }
}
