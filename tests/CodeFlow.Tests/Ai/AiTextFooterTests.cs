using CodeFlow.Ai;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// The provenance stamp under a review or an analysis (`AI-017`).
/// </summary>
/// <remarks>
/// It answered "what produced this and when" and never "at what price", so two runs — one twice as
/// slow as the other — read identically, and comparing them meant opening the CLI's own session
/// files by hand.
/// </remarks>
public sealed class AiTextFooterTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 2, 19, 44, 0, TimeSpan.Zero);

    private static string Stamp(AiUsage? usage) =>
        AiText.StampFooter("cuerpo", "pr-review", "Claude Code", "claude-sonnet-5", When, usage);

    [Fact]
    public void What_the_run_consumed_is_stamped_beside_what_produced_it()
    {
        var stamp = Stamp(new AiUsage(2, 4, 26_635, 11, CostUsd: 0.0157075, DurationMs: 2_239));

        // Billed at full price: fresh input, output and cache writes. Cached reads are stated apart
        // because they cost a fraction and move the most — folding them in would make an agent that
        // re-read the whole repository look like one that read nothing.
        Assert.Contains("· 17 tokens (26,635 desde caché)", stamp, StringComparison.Ordinal);

        // Labelled, not bare. The CLI reports a cost whatever the account is, computed from list
        // prices — so a Pro or Max subscriber, who pays a flat fee and no per-token charge, would
        // read a bare figure as a bill for money nobody is charging them.
        Assert.Contains("· equiv. API USD 0.0157", stamp, StringComparison.Ordinal);
    }

    [Fact]
    public void An_engine_that_reported_nothing_stamps_nothing_extra()
    {
        var stamp = Stamp(null);

        Assert.EndsWith("2026-08-02 19:44", stamp, StringComparison.Ordinal);
        Assert.DoesNotContain("tokens", stamp, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_are_stamped_even_when_the_engine_priced_nothing()
    {
        // The cost is the engine's own figure and is not always there. Nothing here multiplies token
        // counts by a price list this repository would then have to keep current.
        var stamp = Stamp(new AiUsage(100, 50, 0, 0, CostUsd: null, DurationMs: null));

        Assert.Contains("150 tokens", stamp, StringComparison.Ordinal);
        Assert.DoesNotContain("USD", stamp, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stamp_that_was_there_before_is_still_there()
    {
        // `AI-017` is a format other things read; the spend is appended to it, not a rewrite of it.
        var stamp = Stamp(null);

        Assert.StartsWith("cuerpo\n\n---\n🤖 Análisis automatizado (pr-review) · Claude Code (claude-sonnet-5) · ",
            stamp, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_model_still_reads_as_a_sentence()
    {
        var stamp = AiText.StampFooter("cuerpo", "pr-review", "Claude Code", "", When, null);

        Assert.Contains("(modelo predeterminado)", stamp, StringComparison.Ordinal);
    }
}
