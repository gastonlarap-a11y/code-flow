namespace CodeFlow.Ai;

/// <summary>
/// Recognises a quota or billing refusal and tags it for the frontend.
/// </summary>
/// <remarks>
/// Shared by every engine's output interpreter, because "the provider refused because of a limit
/// or your account balance" is not the same as "something is broken" and the UI shows a dedicated
/// notice for it rather than a red error banner.
/// </remarks>
public static class QuotaSignals
{
    /// <summary>
    /// The prefix the frontend looks for. `VERBATIM`.
    /// </summary>
    /// <remarks>
    /// Duplicated in <c>src/lib/claudeError.ts</c>, which splits the remainder into a usage
    /// versus billing case and extracts a reset hint and an action URL. Changing this string
    /// silently downgrades that notice to a generic error — see
    /// <c>docs/business-rules/13-cross-language-contracts.md</c>, <c>XLANG-003</c>.
    /// </remarks>
    public const string Marker = "QUOTA_EXCEEDED::";

    /// <summary>
    /// Phrases that mean a limit or balance refusal. `VERBATIM`.
    /// </summary>
    /// <remarks>
    /// The billing ones matter for the credit-based CLIs: opencode bills per token, so it answers
    /// with "insufficient balance" rather than a rate limit.
    /// </remarks>
    private static readonly string[] Signals =
    [
        "usage limit",
        "rate limit",
        "quota exceeded",
        "resets at",
        "try again in",
        "limit reached",
        "insufficient balance",
        "insufficient credit",
        "out of credit",
        "payment required",
        "billing",
    ];

    /// <summary>Whether a CLI's message reads like a quota or billing refusal.</summary>
    public static bool Matches(string text)
    {
        var lower = text.ToLowerInvariant();
        return Signals.Any(signal => lower.Contains(signal, StringComparison.Ordinal));
    }

    /// <summary>Tags a message with <see cref="Marker"/>, without double-tagging.</summary>
    public static string Mark(string message) =>
        message.StartsWith(Marker, StringComparison.Ordinal) || !Matches(message)
            ? message
            : Marker + message;
}
