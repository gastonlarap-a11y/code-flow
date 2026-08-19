namespace CodeFlow.Ai;

/// <summary>
/// Recognises a CLI that has lost its own login and tags it for the frontend.
/// </summary>
/// <remarks>
/// <para>
/// The four subprocess engines each authenticate outside CodeFlow — Claude against its own OAuth
/// session, Codex against a ChatGPT login, opencode against whatever provider it was configured
/// with, agy against a Google account. None of that is a credential this app holds, so when one of
/// them expires the only thing CodeFlow has is the sentence the CLI printed. Untagged it reaches the
/// user as a raw English error in a red banner, after the clone, the diff and the whole review setup
/// have already been paid for; tagged, the frontend can name the command that fixes it.
/// </para>
/// <para>
/// <b>Consulted only on a failure path</b>, unlike <see cref="QuotaSignals"/>, and that difference is
/// deliberate. Quota matching runs over the engine's whole output including a successful reply, which
/// is how a finished review that merely mentioned "rate limiting" was thrown away as a quota refusal
/// (<c>BUG-AI-b</c>, preserved on purpose). Auth wording is far commoner in a review than quota
/// wording is — "returns 401 Unauthorized" is an ordinary finding — so the same placement here would
/// discard correct reviews routinely. Nothing below is ever asked about the text of a run that
/// succeeded.
/// </para>
/// </remarks>
public static class AuthSignals
{
    /// <summary>
    /// The prefix the frontend looks for. `VERBATIM`.
    /// </summary>
    /// <remarks>
    /// Duplicated in <c>src/lib/claudeError.ts</c>, which strips it and pairs the remainder with the
    /// re-login command for the provider the task is routed to. Changing this string silently
    /// downgrades that notice to a generic error — see
    /// <c>docs/business-rules/13-cross-language-contracts.md</c>, <c>XLANG-003</c>.
    /// </remarks>
    public const string Marker = "AUTH_EXPIRED::";

    /// <summary>
    /// Phrases that mean the CLI is not logged in. `VERBATIM`.
    /// </summary>
    /// <remarks>
    /// Every entry is taken from a payload captured off a real failing run, not invented: Claude's
    /// expired OAuth session, Codex's "not logged in", and opencode's two forms — the 401 event its
    /// API returns and the "auth required" it prints on a failed exit. agy has no captured payload
    /// yet, so it is covered only by whichever of these its wording happens to share.
    /// </remarks>
    private static readonly string[] Signals =
    [
        "failed to authenticate",
        "oauth session expired",
        "session expired",
        "not logged in",
        "auth required",
        "authentication failed",
        "unauthorized",
    ];

    /// <summary>Whether a CLI's failure message reads like a lost login.</summary>
    /// <remarks>
    /// Only ever called with text the engine already judged a failure. Handing it a successful
    /// reply is the mistake this class exists to avoid — see the remarks on the type.
    /// </remarks>
    public static bool Matches(string text)
    {
        var lower = text.ToLowerInvariant();
        return Signals.Any(signal => lower.Contains(signal, StringComparison.Ordinal));
    }
}
