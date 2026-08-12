using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeFlow.Tickets;

/// <summary>One acceptance criterion and what the review concluded about it.</summary>
/// <param name="Id">The criterion's own id, <c>AC-1…AC-N</c>.</param>
/// <param name="Criterion">The criterion's text as the model quoted it.</param>
/// <param name="Verdict">
/// One of <c>cumple</c> · <c>no cumple</c> · <c>parcial</c> · <c>no verificable</c>, normalised.
/// </param>
/// <param name="Evidence">
/// Where in the change the verdict comes from, or the sentence saying there is none. Free text: the
/// prompt asks for <c>path:lines — why</c>, but a verdict is worth keeping even when the model
/// wrote it another way.
/// </param>
/// <param name="Confidence">0–100, or <see langword="null"/> when the line was missing.</param>
public sealed record TicketCriterionVerdict(
    string Id,
    string Criterion,
    string Verdict,
    string Evidence,
    int? Confidence);

/// <summary>The review's answer to "does this branch deliver the ticket".</summary>
/// <param name="Coverage"><c>completa</c> · <c>incompleta</c> · <c>no verificable</c>, normalised.</param>
/// <param name="Relevant">
/// Whether the ticket describes this change at all.
/// </param>
/// <param name="Relevance">
/// Why, in one line. <b>Read before the criteria, because it can invalidate them.</b> A branch can be
/// linked to the wrong work item, and a criterion generic enough — <em>"if the key exists, update
/// it"</em> — matches almost any code that talks to a database. Grading that ticket criterion by
/// criterion produces a confident <c>cumple</c> off a coincidence, which is what happened the first
/// time somebody linked a fixture ticket from another project.
/// </param>
public sealed record TicketCoverage(
    string Coverage,
    string Missing,
    string OutOfScope,
    string Summary,
    bool Relevant,
    string Relevance);

/// <summary>A parsed ticket review: its criteria table and its coverage verdict.</summary>
public sealed record TicketVerdictResult(
    IReadOnlyList<TicketCriterionVerdict> Criteria,
    TicketCoverage? Coverage);

/// <summary>
/// Reads the two sections <c>DEFAULT_TICKET_REVIEW_STANDARD</c> asks the model to close with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why prose and not JSON.</b> A JSON block in the middle of a prose answer is a format the model
/// is producing nowhere else in that same answer, and it is where the output breaks first. The two
/// sections use the grammar the model already emits reliably three lines earlier — a <c>###</c>
/// header with emoji-labelled fields under it — so the whole answer is one shape.
/// </para>
/// <para>
/// <b>Why this cannot collide with the finding parser (<c>XLANG-001</c>).</b>
/// <c>ReviewMemory.ParseFindings</c> and the renderer's <c>parseAnalysis.ts</c> only recognise a
/// <c>###</c> header of the shape <c>### {emoji} [{Severidad} · {Tipo}] {Categoría} · F-NNN</c> —
/// one of three emoji, a bracketed severity, an <c>F-NNN</c> id. <c>### AC-1:</c> carries none of
/// those three, so it reads to them as ordinary prose. On top
/// of that the two readers are handed <em>disjoint slices</em>: <see cref="Split"/> cuts the text at
/// the criteria heading and the finding parser never sees the tail. Belt and braces, deliberately —
/// the tail is the one part of the answer whose shape this feature invented.
/// </para>
/// <para>
/// <b>Tolerant by design.</b> A missing or malformed section yields <see langword="null"/> rather
/// than an error, and the review still renders as an ordinary analysis. Losing a model's whole
/// answer because it forgot a header is the failure mode <c>ReviewMemory</c> already refuses.
/// See <c>docs/business-rules/13-cross-language-contracts.md</c> <c>XLANG-016</c>.
/// </para>
/// </remarks>
internal static partial class TicketVerdict
{
    /// <summary>The heading that opens the criteria table. A byte-level contract.</summary>
    public const string CriteriaHeading = "## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN";

    /// <summary>The heading that opens the coverage verdict. A byte-level contract.</summary>
    public const string CoverageHeading = "## VEREDICTO DE COBERTURA";

    /// <summary>The labels that close a free-text field, so one field never swallows the next.</summary>
    private static readonly string[] FieldLabels =
        ["Veredicto:", "Evidencia:", "Relevancia:", "Cobertura:", "Faltante:", "Fuera de alcance:", "Resumen:"];

    /// <summary>
    /// Cuts a review into the part the finding parsers read and the part this one does.
    /// </summary>
    /// <returns>
    /// The findings text, and the verdict tail — <see langword="null"/> when the review carries no
    /// criteria heading, which is every review this feature did not produce.
    /// </returns>
    public static (string Findings, string? Verdict) Split(string reviewMarkdown)
    {
        if (string.IsNullOrEmpty(reviewMarkdown))
        {
            return (reviewMarkdown ?? string.Empty, null);
        }

        var match = CriteriaHeadingPattern().Match(reviewMarkdown);
        return match.Success
            ? (reviewMarkdown[..match.Index].TrimEnd(), reviewMarkdown[match.Index..])
            : (reviewMarkdown, null);
    }

    /// <summary>Parses the verdict sections out of a whole review, or answers null.</summary>
    public static TicketVerdictResult? Parse(string reviewMarkdown)
    {
        if (Split(reviewMarkdown).Verdict is not { } tail)
        {
            return null;
        }

        var coverageMatch = CoverageHeadingPattern().Match(tail);
        var criteriaText = coverageMatch.Success ? tail[..coverageMatch.Index] : tail;

        var criteria = new List<TicketCriterionVerdict>();
        var headers = CriterionHeaderPattern().Matches(criteriaText);
        for (var i = 0; i < headers.Count; i++)
        {
            var start = headers[i].Index + headers[i].Length;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : criteriaText.Length;
            var block = criteriaText[start..end];

            criteria.Add(new TicketCriterionVerdict(
                Id: headers[i].Groups[1].Value.ToUpperInvariant(),
                Criterion: Clean(headers[i].Groups[2].Value),
                Verdict: NormaliseVerdict(Field(block, "Veredicto:")),
                Evidence: Field(block, "Evidencia:"),
                Confidence: ConfidencePattern().Match(block) is { Success: true } c
                    ? int.Parse(c.Groups[1].ValueSpan, CultureInfo.InvariantCulture)
                    : null));
        }

        return new TicketVerdictResult(criteria, coverageMatch.Success ? ParseCoverage(tail[coverageMatch.Index..]) : null);
    }

    private static TicketCoverage ParseCoverage(string block)
    {
        var relevance = Field(block, "Relevancia:");

        return new TicketCoverage(
            Coverage: NormaliseCoverage(Field(block, "Cobertura:")),
            Missing: Field(block, "Faltante:"),
            OutOfScope: Field(block, "Fuera de alcance:"),
            Summary: Field(block, "Resumen:"),
            // Absent reads as relevant, which is the safe default in both directions: reviews stored
            // before this field existed keep their meaning, and a model that forgot the line does not
            // have its verdict thrown away. Only an explicit "no corresponde" disowns the ticket.
            Relevant: !relevance.StartsWith("no corresponde", StringComparison.OrdinalIgnoreCase),
            Relevance: relevance);
    }

    /// <summary>
    /// The text of one labelled field: the rest of its line plus any lines that follow it, up to the
    /// next label or heading.
    /// </summary>
    /// <remarks>
    /// Line-wise rather than one regex per field because these are the fields most likely to run
    /// long — "what is missing" is a sentence, and a model that wraps it over two lines has not done
    /// anything wrong.
    /// </remarks>
    private static string Field(string block, string label)
    {
        var lines = block.Split('\n');
        var collected = new List<string>();
        var inside = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (inside)
            {
                // A blank line, a heading, the confidence line or the next label all close the
                // field. Anything else is the same sentence wrapped, which is not an error.
                if (trimmed.Length == 0
                    || trimmed.StartsWith('#')
                    || trimmed.StartsWith("🎯", StringComparison.Ordinal)
                    || StartsWithLabel(trimmed))
                {
                    break;
                }

                collected.Add(trimmed);
                continue;
            }

            if (!trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            inside = true;
            collected.Add(trimmed[label.Length..].Trim());
        }

        return Clean(string.Join(' ', collected.Where(l => l.Length > 0)));
    }

    private static bool StartsWithLabel(string line) =>
        FieldLabels.Any(label => line.StartsWith(label, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps whatever the model wrote onto the four literals, defaulting to <c>no verificable</c>.
    /// </summary>
    /// <remarks>
    /// The default is the conservative one on purpose: the prompt's own standing order is to prefer
    /// a false alarm to approving incomplete work, and a verdict this parser could not read is not
    /// evidence that a criterion was met. The order of the tests matters — <c>no cumple</c> and
    /// <c>no verificable</c> both begin with "no", and both contain "cumple" nowhere else.
    /// </remarks>
    private static string NormaliseVerdict(string raw)
    {
        var value = Clean(raw).ToLowerInvariant();

        if (value.StartsWith("cumple", StringComparison.Ordinal))
        {
            return "cumple";
        }

        if (value.StartsWith("no cumple", StringComparison.Ordinal))
        {
            return "no cumple";
        }

        if (value.StartsWith("parcial", StringComparison.Ordinal))
        {
            return "parcial";
        }

        return "no verificable";
    }

    /// <summary>Maps the coverage word, defaulting to <c>no verificable</c> for the same reason.</summary>
    private static string NormaliseCoverage(string raw)
    {
        var value = Clean(raw).ToLowerInvariant();

        if (value.StartsWith("completa", StringComparison.Ordinal))
        {
            return "completa";
        }

        return value.StartsWith("incompleta", StringComparison.Ordinal) ? "incompleta" : "no verificable";
    }

    /// <summary>
    /// Strips the emphasis the model wraps a value in despite being told not to.
    /// </summary>
    /// <remarks>
    /// The same allowance <c>parseAnalysis.ts</c> makes for the location line, and for the same
    /// reason: everywhere else in the answer backticks and bold are welcome, so a model that writes
    /// <c>Veredicto: **cumple**</c> is following the habit the rest of the prompt taught it.
    /// </remarks>
    private static string Clean(string value) => value.Trim().Trim('*', '`', '_', ' ', ':').Trim();

    // `[ \t]*` rather than `\s*`, and it is load-bearing: `\s` matches a newline, so `^\s*##` starts
    // its match on the blank line *above* the heading and the split then carries that newline into
    // the verdict slice — enough to fail an exact comparison of the findings half.
    [GeneratedRegex(@"(?m)^[ \t]*##[ \t]*VERIFICACI[OÓ]N DE CRITERIOS DE ACEPTACI[OÓ]N[ \t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex CriteriaHeadingPattern();

    [GeneratedRegex(@"(?m)^[ \t]*##[ \t]*VEREDICTO DE COBERTURA[ \t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex CoverageHeadingPattern();

    [GeneratedRegex(@"(?m)^[ \t]*###[ \t]*(AC-\d+)[ \t]*:?[ \t]*([^\n]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CriterionHeaderPattern();

    [GeneratedRegex(@"🎯\s*Confianza:\s*(\d{1,3})")]
    private static partial Regex ConfidencePattern();
}
