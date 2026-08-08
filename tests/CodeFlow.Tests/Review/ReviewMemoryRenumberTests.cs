using CodeFlow.Review;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// Aligning the <c>F-NNN</c> the user reads with the id thread reuse keys on.
/// </summary>
/// <remarks>
/// <c>DIVERGENCE-REVIEW-a</c>. The model writes its own numbering from <c>F-001</c> every run while
/// <see cref="ReviewMemory.Reconcile"/> assigns stable ids separately, so the number in the markdown
/// could name a different finding from the one a click acted on. WF-PR-REVIEWER's
/// <c>report-standard.md</c> §3.1 has an engine assign ids and render the report — one source of
/// truth by construction — which is what settled that the drift was never intended.
/// </remarks>
public sealed class ReviewMemoryRenumberTests
{
    private const string Markdown = """
        ### 🚨 [Alta·Bug] Seguridad · F-001
        Se filtra el token.
        📍 Ubicación: src/a.ts:10

        ### ⚠️ [Media·Smell] Rendimiento · F-002
        Consulta en bucle.
        📍 Ubicación: src/b.ts:20
        """;

    [Fact]
    public void The_headers_take_the_ids_reconciliation_assigned()
    {
        var parsed = ReviewMemory.ParseFindings(Markdown);
        var reconciled = new[]
        {
            parsed[0] with { Id = "F-007" },
            parsed[1] with { Id = "F-008" },
        };

        var rewritten = ReviewMemory.RenumberHeaders(Markdown, parsed, reconciled);

        Assert.Contains("Seguridad · F-007", rewritten, StringComparison.Ordinal);
        Assert.Contains("Rendimiento · F-008", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("F-001", rewritten, StringComparison.Ordinal);

        // Re-parsing has to agree with what reconciliation decided — that agreement is the whole
        // point, and asserting the string alone would not prove the parser still reads it.
        Assert.Equal(["F-007", "F-008"], ReviewMemory.ParseFindings(rewritten).Select(f => f.Id));
    }

    [Fact]
    public void Nothing_but_the_id_moves()
    {
        // The header format is XLANG-001, a three-way contract with the prompt and the TypeScript
        // parser. Touching the emoji, the severity, the category or the spacing would make reviews
        // parse to zero findings on the frontend, silently.
        var parsed = ReviewMemory.ParseFindings(Markdown);
        var rewritten = ReviewMemory.RenumberHeaders(
            Markdown, parsed, [parsed[0] with { Id = "F-007" }, parsed[1] with { Id = "F-008" }]);

        Assert.Equal(
            Markdown.Replace("F-001", "F-007", StringComparison.Ordinal)
                .Replace("F-002", "F-008", StringComparison.Ordinal),
            rewritten);
    }

    [Fact]
    public void An_id_mentioned_in_prose_is_left_alone()
    {
        // A paragraph naming F-001 is the model's argument, not a reference this engine owns.
        // Rewriting it would be editing what the reviewer said.
        const string WithProse = """
            ### 🚨 [Alta·Bug] Seguridad · F-001
            Igual que F-001 en la revisión anterior.
            📍 Ubicación: src/a.ts:10
            """;

        var parsed = ReviewMemory.ParseFindings(WithProse);
        var rewritten = ReviewMemory.RenumberHeaders(WithProse, parsed, [parsed[0] with { Id = "F-004" }]);

        Assert.Contains("Seguridad · F-004", rewritten, StringComparison.Ordinal);
        Assert.Contains("Igual que F-001 en la revisión anterior.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_that_does_not_line_up_changes_nothing()
    {
        // The pairing is positional, and positional pairings rot silently. If the identities stop
        // agreeing, the text is returned untouched: a wrong number is worse than a stale one,
        // because a wrong one sends a reply to another finding's thread.
        var parsed = ReviewMemory.ParseFindings(Markdown);
        var shuffled = new[]
        {
            parsed[1] with { Id = "F-007" },
            parsed[0] with { Id = "F-008" },
        };

        Assert.Equal(Markdown, ReviewMemory.RenumberHeaders(Markdown, parsed, shuffled));
    }

    [Fact]
    public void More_reconciled_findings_than_headers_is_normal_and_fine()
    {
        // Reconcile returns carried-forward findings after the current ones, so the merged list is
        // routinely longer than the parse. Only the leading run has to correspond.
        var parsed = ReviewMemory.ParseFindings(Markdown);
        var reconciled = new[]
        {
            parsed[0] with { Id = "F-007" },
            parsed[1] with { Id = "F-008" },
            parsed[0] with { Id = "F-009", Estado = MemoryFinding.Resolved },
        };

        Assert.Contains("Seguridad · F-007", ReviewMemory.RenumberHeaders(Markdown, parsed, reconciled),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_review_with_no_findings_is_returned_as_it_came()
    {
        const string Empty = "No se encontraron hallazgos.";

        Assert.Equal(Empty, ReviewMemory.RenumberHeaders(Empty, [], []));
    }
}
