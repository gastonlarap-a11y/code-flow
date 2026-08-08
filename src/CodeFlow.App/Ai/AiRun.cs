namespace CodeFlow.Ai;

/// <summary>One finished AI run: the reply plus the metadata a caller may want to keep.</summary>
/// <param name="Text">The answer. Never one of the streamed <c>ai:output</c> lines — those are the activity log.</param>
/// <param name="SessionId">
/// Session to resume on the next turn. Null for engines and turns that report none.
/// </param>
/// <param name="Model">
/// The model the CLI actually ran, when it reported exactly one. Null when the run fanned out
/// across several or the CLI did not say — callers fall back to the configured setting rather than
/// guessing.
/// </param>
/// <param name="Usage">
/// What the run cost, when the engine said. Null rather than zeroes for the engines that report
/// nothing — a zero would read as a free run instead of an unmeasured one.
/// </param>
public sealed record AiRun(string Text, string? SessionId, string? Model, AiUsage? Usage = null);

/// <summary>
/// What one run consumed.
/// </summary>
/// <remarks>
/// <para>
/// Kept because none of it was: a review that took six minutes and one that took three were
/// indistinguishable in cost, and answering "did that get cheaper?" meant reading the CLI's own
/// session files by hand. The four token counts are the ones that bill differently — cached reads
/// cost a fraction of fresh input, so summing them into one number would hide the difference that
/// matters most.
/// </para>
/// <para>
/// <paramref name="CostUsd"/> is what the engine itself reported. It is a figure from the provider,
/// not a calculation of ours: pricing changes, and a stale multiplier in this codebase would be
/// worse than no number at all.
/// </para>
/// </remarks>
/// <param name="InputTokens">Fresh input, excluding anything served from cache.</param>
/// <param name="OutputTokens">What the model wrote.</param>
/// <param name="CacheReadTokens">Input served from cache, billed at a fraction of fresh input.</param>
/// <param name="CacheWriteTokens">Input written into the cache, billed above fresh input.</param>
/// <param name="CostUsd">The engine's own figure, when it reported one.</param>
/// <param name="DurationMs">The engine's own figure for how long it ran, when it reported one.</param>
public sealed record AiUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    double? CostUsd,
    long? DurationMs);

/// <summary>An engine reported failure. Its message reaches the renderer as a rejected promise.</summary>
/// <remarks>
/// The message is user-facing and, for several engines, load-bearing: the quota marker the
/// frontend keys off is a prefix on it. Reformatting one breaks a feature silently.
/// </remarks>
public sealed class AiRunFailedException(string message) : Exception(message);
