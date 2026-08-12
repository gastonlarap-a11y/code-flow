using CodeFlow.Tickets;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// Turning a verdict into what an Azure comment actually displays.
/// </summary>
/// <remarks>
/// Azure work item comments are rich text. Posting the verdict as written would put
/// <c>## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN</c> and <c>**cumple**</c> on somebody's board with
/// their punctuation showing — permanently, because a comment is what a person reads later. The
/// subset covered is fixed by <c>XLANG-016</c> and <c>XLANG-001</c>, which is what makes converting
/// it safe rather than a general markdown renderer's problem.
/// </remarks>
public sealed class TicketCommentTests
{
    [Fact]
    public void The_two_verdict_headings_become_headings()
    {
        var html = TicketComment.ToHtml("## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN\n### AC-1: la tabla");

        // h3 and h4, not h1/h2: the comment is nested inside the work item's own page, and a heading
        // that outranks the item's title reads as a mistake.
        Assert.Equal("<h3>VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN</h3><h4>AC-1: la tabla</h4>", html);
    }

    [Fact]
    public void Bold_and_inline_code_survive_as_themselves()
    {
        var html = TicketComment.ToHtml("Veredicto: **cumple** en `TicketStore.cs:40`");

        Assert.Equal("<div>Veredicto: <b>cumple</b> en <code>TicketStore.cs:40</code></div>", html);
    }

    [Fact]
    public void Two_bold_runs_on_one_line_stay_two()
    {
        // A greedy pattern would merge them and swallow the words in between.
        var html = TicketComment.ToHtml("**cumple** y también **no verificable**");

        Assert.Equal("<div><b>cumple</b> y también <b>no verificable</b></div>", html);
    }

    [Fact]
    public void A_diff_quoted_in_the_verdict_cannot_close_a_tag()
    {
        // The single reason escaping happens before anything else: review text quotes code, and code
        // contains angle brackets. Without this the comment could carry markup the verdict never
        // meant — on a page other people read.
        var html = TicketComment.ToHtml("Evidencia: `if (a < b && c > d)` en <script>alert(1)</script>");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("a &lt; b &amp;&amp; c &gt; d", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_footers_rule_becomes_a_rule()
    {
        Assert.Equal("<hr><div>🤖 Análisis automatizado</div>", TicketComment.ToHtml("---\n🤖 Análisis automatizado"));
    }

    [Fact]
    public void A_bullet_keeps_its_bullet()
    {
        Assert.Equal("<div>• primero</div><div>• segundo</div>", TicketComment.ToHtml("- primero\n- segundo"));
    }

    [Fact]
    public void Blank_lines_are_dropped_rather_than_becoming_empty_boxes()
    {
        // Markdown uses them to separate blocks; the div-per-line rendering already separates, so
        // keeping them would double every gap.
        Assert.Equal("<div>uno</div><div>dos</div>", TicketComment.ToHtml("uno\n\n\ndos"));
    }

    [Fact]
    public void Windows_line_endings_do_not_leave_stray_carriage_returns()
    {
        Assert.Equal("<div>uno</div><div>dos</div>", TicketComment.ToHtml("uno\r\ndos"));
    }
}
