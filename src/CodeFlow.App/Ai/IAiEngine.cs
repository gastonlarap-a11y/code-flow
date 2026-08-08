using System.Diagnostics;

namespace CodeFlow.Ai;

/// <summary>
/// One AI engine: how to launch it and how to read what it produced.
/// </summary>
/// <remarks>
/// <para>
/// An interface with six real implementations, which is the bar <c>.claude/rules/dotnet.md</c> sets —
/// it names the agent adapters explicitly as one of the two places that qualify. Everything
/// provider-neutral (spawning, piping, cancellation, quota detection) lives outside it, in
/// <see cref="AiRunRegistry"/>.
/// </para>
/// <para>
/// The defaults on this interface are 1.7.2's trait defaults, and they encode which
/// behaviour is the exception: only one engine overrides <see cref="StdinPayload"/>, only two
/// override <see cref="Transport"/>, only one is non-agentic. See
/// <c>docs/business-rules/05-ai-engines.md</c>.
/// </para>
/// </remarks>
public interface IAiEngine
{
    /// <summary>The provider id this engine is stored and routed under.</summary>
    string Id { get; }

    /// <summary>Human label for footers and the chat chip, e.g. <c>"Claude Code"</c>.</summary>
    string Label { get; }

    /// <summary>Binary to run when the user has configured no path.</summary>
    /// <remarks>For an HTTP transport this is the base URL, not a path.</remarks>
    string DefaultBinary { get; }

    /// <summary>
    /// Fast model for mechanical tasks, used for commit messages regardless of the configured
    /// review model. Empty means "let the CLI pick".
    /// </summary>
    string CommitMessageModel => string.Empty;

    /// <summary>How this engine reaches its model.</summary>
    Transport Transport => Ai.Transport.Subprocess.Instance;

    /// <summary>Whether this engine can run an agentic tool loop.</summary>
    /// <remarks>
    /// A plain completion endpoint cannot, so "fix with AI" and MCP are hidden for it in the UI
    /// and refused defensively in the backend rather than failing halfway through a run.
    /// </remarks>
    bool Agentic => true;

    /// <summary>
    /// Whether the engine carries a conversation forward on its own side between turns.
    /// </summary>
    /// <remarks>
    /// When it does not, chat re-sends the system prompt and project context every turn instead of
    /// only on the first.
    /// </remarks>
    bool ResumesSessions => true;

    /// <summary>
    /// The write-capable tool set for "fix with AI" — provider-specific names.
    /// </summary>
    /// <remarks>
    /// Fixed per engine rather than user-configurable: clicking "fix" is itself the write opt-in,
    /// so there is no second setting to get wrong.
    /// </remarks>
    IReadOnlyList<string> FixTools => [];

    /// <summary>
    /// Args that make the binary print its available models, one id per line, or null when the CLI
    /// has no such command.
    /// </summary>
    /// <remarks>
    /// This is how the model picker shows what is actually installed rather than a hardcoded guess.
    /// An engine returning null falls back to the frontend's curated list.
    /// </remarks>
    IReadOnlyList<string>? ListModelsArgs => null;

    /// <summary>
    /// Models this engine can enumerate without running anything, typically from a catalogue its
    /// CLI already keeps on disk.
    /// </summary>
    /// <remarks>
    /// Checked before <see cref="ListModelsArgs"/>, so an engine with no listing subcommand can
    /// still offer a current list without paying for a process spawn.
    /// </remarks>
    IReadOnlyList<string>? CachedModels() => null;

    /// <summary>Builds the invocation.</summary>
    /// <remarks>
    /// <paramref name="binary"/> is already resolved. Implementations set arguments and working
    /// directory only — the caller owns the stdio pipes and the augmented <c>PATH</c>.
    /// </remarks>
    ProcessStartInfo BuildCommand(string binary, AiInvocation invocation);

    /// <summary>Turns a finished run into its reply, or into a user-facing error.</summary>
    /// <remarks>
    /// <paramref name="stdout"/> and <paramref name="stderr"/> arrive ANSI-stripped. Throwing
    /// <see cref="AiRunFailedException"/> is how an engine reports a failure the user should see;
    /// the distinction between "the process failed" and "the CLI reported an error in valid
    /// output" is per-engine, which is exactly why this is not shared code.
    /// </remarks>
    AiRun Interpret(bool success, string statusLabel, string stdout, string stderr);

    /// <summary>What gets piped to the process's stdin.</summary>
    /// <remarks>
    /// <para>
    /// The default is the invocation's data payload, which is what every engine that takes its
    /// instructions as arguments wants. Overridden two ways: by the engine whose CLI takes a fixed
    /// pointer sentence on argv and reads the real brief from stdin (<c>AI-004</c>), and by the two
    /// that fold the data into a brief of their own and would otherwise be sent it twice.
    /// </para>
    /// <para>
    /// <b>Empty means "this CLI does not read stdin"</b>, and the runner reads it that way: an
    /// engine that was handed a payload and stopped reading before all of it arrived answered from
    /// an unknown fraction of it, and that answer is refused (<c>AI-048</c>). Leaving a payload here
    /// for a CLI that ignores it turns that check into noise.
    /// </para>
    /// </remarks>
    string StdinPayload(AiInvocation invocation) => invocation.StdinContent;
}
