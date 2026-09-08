using System.Security.Cryptography;
using System.Text;

namespace CodeFlow.Storage;

/// <summary>
/// SHA-256 digests of every built-in prompt text CodeFlow has shipped and seeded into
/// <c>workspace_prompts</c>, grouped by prompt kind.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Migrations"/>' <c>RefreshUneditedSeededPrompts</c> step uses these to tell a
/// pristine seeded copy from one the user has edited. A row whose content hashes to one of these
/// is a former built-in default nobody touched, so replacing it with the current default is safe;
/// a row that matches nothing here is the user's own methodology and is left alone.
/// </para>
/// <para>
/// Record the outgoing digest here <b>before</b> changing a <c>Ai/Prompts/*.txt</c> that
/// <c>Migrations.BackfillWorkspacePrompts</c> seeds — the mechanism is "does this row still equal a
/// text we used to ship". The digest is over the file's UTF-8 bytes with no BOM, which is exactly
/// what <see cref="CodeFlow.Ai.Prompts"/> loads and what the backfill stored. Only the kinds whose
/// built-in text actually changes get an entry; <c>pr_description</c> is seeded too but has not
/// moved, and <c>analyze_template</c> / <c>commit_template</c> live in the shared settings table
/// with no row until the user saves one, so an unedited install already falls back to the current
/// built-in.
/// </para>
/// </remarks>
internal static class SeededPromptHistory
{
    /// <summary>Prior built-in <c>DEFAULT_PR_REVIEW_STANDARD.txt</c> digests.</summary>
    /// <remarks>
    /// <c>6b8bdda6…</c> — v2.5.1, the SonarQube taxonomy before the WF-PR-REVIEWER re-sync that
    /// added the five-emoji severity scale, the <c>🚦 Quality Gate</c> line, the
    /// <c>👍 Lo que está bien</c> / <c>🗒️ Notas</c> sections and the English category vocabulary.
    /// </remarks>
    public static readonly IReadOnlySet<string> ReviewStandard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "6b8bdda6da739ae4f60809830e7854a91278d0a32862a8e80385ac76d1f3d0c4",
    };

    /// <summary>Prior built-in <c>DEFAULT_TICKET_REVIEW_STANDARD.txt</c> digests.</summary>
    /// <remarks><c>a5cb429d…</c> — v2.5.1, paired with the review standard above.</remarks>
    public static readonly IReadOnlySet<string> TicketReviewStandard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a5cb429d5f7e034aec95e3e381164cc8198293f339b6b6c606653ebc6cc1756c",
    };

    /// <summary>Lower-case hex SHA-256 of a stored prompt string, matching the digests above.</summary>
    public static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
