using CodeFlow.Ai;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// The built-in prompt texts, and the one contract two of them share.
/// See <c>docs/business-rules/13-cross-language-contracts.md</c> <c>XLANG-001</c>, <c>XLANG-016</c>.
/// </summary>
public sealed class PromptsTests
{
    /// <summary>Where the shared half of the two review standards begins, verbatim.</summary>
    private const string SharedBlockStart = "## Review lenses (read the diff under each one)";

    /// <summary>
    /// The load-bearing test of the ticket-review prompt: its finding format is not <em>similar</em>
    /// to the PR standard's, it is the same bytes.
    /// </summary>
    /// <remarks>
    /// Two copies of a contract that two parsers match on is a real hazard — <c>ReviewMemory</c> and
    /// <c>parseAnalysis.ts</c> both key on <c>📍 Ubicación</c>, <c>🎯 Confianza</c>, the three emoji
    /// and the <c>F-NNN</c> id. Editing one copy for clarity is the plausible mistake, and it would
    /// not fail anywhere: the model would simply emit findings the renderer cannot read, and the
    /// review would render as one wall of prose. So the identity is asserted rather than trusted.
    /// <para>
    /// The block covers the lenses, the taxonomy, the discard list, the A–E ratings, the Quality Gate
    /// and the whole output format — everything from the lenses heading to the end of the file.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_two_review_standards_share_the_finding_format_verbatim()
    {
        var shared = Prompts.DefaultPrReviewStandard[Prompts.DefaultPrReviewStandard.IndexOf(
            SharedBlockStart, StringComparison.Ordinal)..];

        Assert.True(shared.Length > 4000, "the shared block should be the whole second half of the PR standard");
        Assert.Contains(shared, Prompts.DefaultTicketReviewStandard, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ticket standard's own half: the parts that make it a ticket review rather than a second
    /// PR review.
    /// </summary>
    [Theory]
    [InlineData("## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN")]
    [InlineData("## VEREDICTO DE COBERTURA")]
    [InlineData("`cumple` · `no cumple` · `parcial` · `no verificable`")]
    [InlineData("`completa` · `incompleta` · `no verificable`")]
    [InlineData("CRITERIA MODE:")]
    [InlineData("Never invent a criterion")]
    public void The_ticket_standard_carries_its_own_contract(string fragment) =>
        Assert.Contains(fragment, Prompts.DefaultTicketReviewStandard, StringComparison.Ordinal);

    /// <summary>
    /// The two headers the standard asks for are the two constants the parser matches on.
    /// </summary>
    /// <remarks>
    /// Written the other way round on purpose: the prompt is what the model reads and the constants
    /// are what the app reads, and a test that took both from the same place would prove nothing.
    /// </remarks>
    [Fact]
    public void The_verdict_headers_the_prompt_asks_for_are_the_ones_the_parser_looks_for()
    {
        Assert.Contains(
            CodeFlow.Tickets.TicketVerdict.CriteriaHeading,
            Prompts.DefaultTicketReviewStandard,
            StringComparison.Ordinal);
        Assert.Contains(
            CodeFlow.Tickets.TicketVerdict.CoverageHeading,
            Prompts.DefaultTicketReviewStandard,
            StringComparison.Ordinal);
    }
}
