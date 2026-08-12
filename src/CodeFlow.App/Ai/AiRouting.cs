using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Ai;

/// <summary>
/// How a task picks its provider, model, binary and tool list out of <c>app_settings</c>.
/// </summary>
/// <remarks>
/// <para>
/// This lives with the AI code rather than with settings storage on purpose: it reads
/// <c>app_settings</c>, but what it encodes is the AI dispatch policy, and the engines in
/// <c>Ai/Engines/</c> are its only consumers. See
/// <c>docs/business-rules/13-cross-language-contracts.md</c> <c>XLANG-004</c> and <c>XLANG-005</c>.
/// </para>
/// <para>
/// The renderer re-implements this same cascade in <c>src/state/aiProviderStore.ts</c> so the
/// settings screen can show what will actually run. The two disagree today in exactly one place —
/// <c>BUG-XLANG-a</c>, the commit-task step below — and that disagreement is reproduced, not fixed.
/// </para>
/// </remarks>
internal static class AiRouting
{
    /// <summary>
    /// The nine task keys, verbatim.
    /// </summary>
    /// <remarks>
    /// These strings are the settings namespace: they appear inside every key this class builds and
    /// inside the renderer's own copy in <c>src/lib/aiTasks.ts</c>. Renaming one silently orphans a
    /// user's stored routing rather than failing anywhere.
    /// <para>
    /// <c>ticket_review</c> is last because it is the newest, and its position is not load-bearing:
    /// the renderer orders its own table. It earns a key of its own rather than riding on
    /// <c>review</c> because judging a branch against a work item's acceptance criteria is the one
    /// task where a user may reasonably want a different — usually larger — model than the one that
    /// reads a pull request's diff (<c>WI-011</c>).
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> Tasks =
        ["chat", "commit", "analyze", "review", "pr_description", "fix", "conflict", "inline", "ticket_review"];

    /// <summary>The provider used when nothing is configured at all.</summary>
    public const string FallbackProvider = EngineCatalog.FallbackProvider;

    /// <summary>Resolves everything a run of <paramref name="task"/> needs.</summary>
    public static AiConfig Resolve(SqliteConnection connection, string task)
    {
        var provider = ProviderFor(connection, task);
        return ResolveFor(connection, provider, ModelFor(connection, provider, task), task);
    }

    /// <summary>
    /// The tasks that read code to judge it, rather than to change it or to talk about it.
    /// </summary>
    /// <remarks>
    /// Only these get the default below. A chat turn is a conversation with the repository and a fix
    /// is an edit to it — bounding either would be taking away something the user asked for. A
    /// review is neither: it is handed the change it is meant to judge, and every command it runs on
    /// top of that is time and tokens spent re-deriving what it already has. It is a floor rather
    /// than the last word: a review narrows it further by level, through <see cref="Bound"/>.
    /// <para>
    /// <c>ticket_review</c> belongs here for a reason the other two do not have: it is asked whether
    /// a criterion is <em>met</em>, and a criterion is regularly satisfied by code the diff does not
    /// touch — a helper the change calls, a test that already covered the case. Without the three
    /// read tools it would have to answer <c>no verificable</c> for those, which is the honest answer
    /// to a question it was never given the means to ask.
    /// </para>
    /// </remarks>
    private static readonly string[] Judging = ["analyze", "review", "ticket_review"];

    /// <summary>
    /// What a judging run may reach for when nobody has said otherwise.
    /// </summary>
    /// <remarks>
    /// The three the settings screen already ticks as recommended — but ticking them there saves
    /// nothing unless a checkbox is touched, so an install where the user never opened that row sent
    /// no list at all and the CLI applied its own defaults. Measured on real reviews of this
    /// repository: the agent called <c>Bash</c> eleven and seventeen times against two or three
    /// <c>Read</c>s, reading over two million cached tokens to judge a diff it had already been
    /// handed. <c>Bash</c> is left out deliberately — reading the code around a change is the job,
    /// running commands to do it is the expensive one.
    /// </remarks>
    internal static readonly string[] RecommendedTools = ["Read", "Grep", "Glob"];

    /// <summary>
    /// Narrows a resolved config's toolset, unless the user has chosen one for this provider.
    /// </summary>
    /// <remarks>
    /// An <em>empty</em> list is not the absence of one: it means this run gets no tools at all, and
    /// the engines pass it on as such. That distinction is what lets a review that was already
    /// handed the code around every change say so, instead of leaving the CLI to apply its own
    /// defaults. A user who has set the toolset in Settings keeps it either way — this is a default,
    /// not a policy.
    /// </remarks>
    public static AiConfig Bound(SqliteConnection connection, AiConfig config, IReadOnlyList<string> tools) =>
        AllowedTools(connection, config.Provider) is null ? config with { AllowedTools = tools } : config;

    /// <summary>
    /// Resolves a run that already knows its provider and model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path an active workspace agent takes: its provider and model replace steps 1 and 2 of
    /// the cascade outright, so this reads only the provider's binary and tool settings and takes
    /// the model verbatim. It never consults the per-task keys.
    /// </para>
    /// <para>
    /// <paramref name="task"/> is required rather than optional because leaving it out is exactly
    /// the bug this closes: a workspace agent driving a review reached here and skipped the toolset
    /// default entirely, running with everything the CLI offers while the ordinary route was bounded
    /// to three tools. The two routes now decide it in the same place.
    /// </para>
    /// </remarks>
    public static AiConfig ResolveFor(SqliteConnection connection, string provider, string model, string task)
    {
        // Resolved once and carried on the config: for the OpenAI-compatible provider this reads the
        // API key out of the OS keychain, which is not something to repeat per property access.
        var engine = EngineCatalog.EngineFor(provider);
        var configured = AllowedTools(connection, provider);

        return new AiConfig(
            engine,
            provider,
            model,
            BinaryPath: NonBlank(Settings.GetSetting(connection, $"{provider}_binary_path")) ?? engine.DefaultBinary,
            AllowedTools: configured ?? (Judging.Contains(task) ? RecommendedTools : null));
    }

    /// <summary>Which provider handles a task.</summary>
    /// <remarks>
    /// Per-task override, then the global active provider, then <c>claude</c>. A blank stored value
    /// counts as unset at every step — the settings UI writes an empty string to clear a row rather
    /// than deleting it.
    /// </remarks>
    public static string ProviderFor(SqliteConnection connection, string task) =>
        NonBlank(Settings.GetSetting(connection, $"ai_provider_{task}"))
        ?? NonBlank(Settings.GetSetting(connection, "ai_provider"))
        ?? FallbackProvider;

    /// <summary>Which model a provider runs a task on.</summary>
    /// <remarks>
    /// Per-task override first. Then, <b>for the commit task only</b>, the engine's own
    /// commit-message model when it defines one — a small mechanical task that does not need the
    /// review model. Then the provider's base model, and finally the empty string, which the
    /// engines read as "let the CLI choose".
    /// </remarks>
    public static string ModelFor(SqliteConnection connection, string provider, string task)
    {
        if (NonBlank(Settings.GetSetting(connection, $"{provider}_{task}_model")) is { } perTask)
        {
            return perTask;
        }

        // Length, not blankness: these are compile-time constants that are either a real model id
        // or exactly empty, and 1.7.2 tests the same way.
        if (task == "commit" && EngineDefaults(provider).CommitMessageModel is { Length: > 0 } commitModel)
        {
            return commitModel;
        }

        return Settings.GetSetting(connection, $"{provider}_model") ?? string.Empty;
    }

    /// <summary>
    /// Resolves a template that is shared across providers, honouring its legacy key.
    /// </summary>
    /// <remarks>
    /// The three provider-independent templates — <c>commit_template</c>,
    /// <c>resolve_conflict_template</c>, <c>analyze_template</c> — were once stored under a
    /// <c>claude_</c> prefix. The unprefixed key wins; the legacy key is consulted only when the new
    /// one is absent <em>or blank</em>, because a renamed settings key has no migration path and
    /// would otherwise strand whatever the user had written. Neither present means the empty string,
    /// which each engine reads as "use your own built-in".
    /// <para>
    /// The blank test applies to the current key only. Once the fallback fires, whatever the legacy
    /// key holds is returned as-is — including a whitespace-only value, which 1.7.2 does not
    /// filter a second time.
    /// </para>
    /// </remarks>
    public static string SharedTemplate(SqliteConnection connection, string key) =>
        NonBlank(Settings.GetSetting(connection, key))
        ?? Settings.GetSetting(connection, $"claude_{key}")
        ?? string.Empty;

    /// <summary>The provider's allow-listed tools, comma separated, blanks dropped.</summary>
    /// <remarks>
    /// <b>Absent and blank are different answers here</b>, and this is the one place in the cascade
    /// where that is true. No row at all means the user has not chosen: chat, fixes and the rest run
    /// on whatever the engine defaults to, and the judging tasks substitute a default. A row holding
    /// the empty string is what the settings screen writes when every checkbox is cleared, and it
    /// means <em>no tools</em> — a choice the code could already read and, until the engines learned
    /// to send an empty list, could not act on.
    /// </remarks>
    private static string[]? AllowedTools(SqliteConnection connection, string provider) =>
        Settings.GetSetting(connection, $"{provider}_allowed_tools") is { } configured
            ? configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : null;

    /// <summary>What an engine supplies when a setting does not.</summary>
    /// <remarks>
    /// An unrecognised provider resolves to Claude here, exactly as everywhere else — see
    /// <see cref="EngineCatalog.EngineFor"/> and <c>AI-001</c>. There is deliberately no "unknown
    /// provider" branch: the cascade must always end with something runnable.
    /// </remarks>
    private static (string Binary, string CommitMessageModel) EngineDefaults(string provider)
    {
        var engine = EngineCatalog.EngineFor(provider);
        return (engine.DefaultBinary, engine.CommitMessageModel);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Everything an engine invocation needs, once the cascade has run.</summary>
/// <param name="Engine">The adapter that will run this invocation.</param>
/// <param name="Provider">
/// The provider id the cascade resolved to — kept alongside <paramref name="Engine"/> because the
/// two differ for an unrecognised provider, which runs on Claude while still being recorded under
/// the name it was configured with. It is also what a persisted turn is stamped with, since the
/// setting it came from is a moving target.
/// </param>
/// <param name="AllowedTools">
/// The tools this run may reach for. <see langword="null"/> leaves the engine's own defaults alone;
/// an empty list is a decision, and means none.
/// </param>
internal sealed record AiConfig(
    IAiEngine Engine,
    string Provider,
    string Model,
    string BinaryPath,
    IReadOnlyList<string>? AllowedTools);
