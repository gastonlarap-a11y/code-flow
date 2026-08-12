using CodeFlow.Tickets;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// The rich-text conversion a ticket's mirror is written through.
/// </summary>
/// <remarks>
/// The fixtures are shaped like the real thing: the markup here was taken from a live Azure Boards
/// work item, <c>style</c> noise and all. A converter is only worth what its inputs are worth.
/// </remarks>
public sealed class TicketHtmlTests
{
    // ---------- the measurement that decides whether a field is a requirement ----------

    [Fact]
    public void A_field_holding_only_a_dash_measures_as_one_character_not_nineteen()
    {
        // The real acceptance-criteria field of a real work item. Counting the raw string would call
        // this filled in, and the AI would be asked to review a branch against a hyphen.
        const string Field = "<div><b>-</b> </div>";

        Assert.Equal(20, Field.Length);
        Assert.Equal(1, TicketHtml.SubstanceLength(Field));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("<div>&nbsp;</div>", 0)]
    [InlineData("<div><br><span style=\"color:red\"></span></div>", 0)]
    public void Markup_without_content_measures_as_empty(string? html, int expected)
    {
        Assert.Equal(expected, TicketHtml.SubstanceLength(html));
    }

    [Fact]
    public void Plain_text_keeps_words_apart_across_block_boundaries()
    {
        // Without this, "<div>uno</div><div>dos</div>" measures as "unodos" and any later word
        // matching reads one token that does not exist.
        Assert.Equal("uno dos", TicketHtml.PlainText("<div>uno</div><div>dos</div>"));
    }

    // ---------- entities ----------

    [Fact]
    public void Entities_are_resolved_including_the_non_breaking_space_the_editor_emits()
    {
        // &nbsp; is everywhere in the real input. Left encoded it becomes a character that looks like
        // a space and matches nothing that expects one.
        Assert.Equal("a b & c < d", TicketHtml.ToMarkdown("a&nbsp;b &amp; c &lt; d"));
    }

    [Fact]
    public void A_less_than_sign_in_prose_does_not_swallow_the_rest_of_the_field()
    {
        Assert.Contains("si a < b entonces", TicketHtml.ToMarkdown("<div>si a < b entonces</div>"), StringComparison.Ordinal);
    }

    // ---------- lists, including the nested ones the real input has ----------

    [Fact]
    public void A_nested_list_keeps_its_levels()
    {
        // The real description nests a <ul> inside an <li>. Flattening it loses which rule is a
        // sub-case of which, which is exactly what an acceptance criterion hangs on.
        const string Html = """
            <ul style="padding:0px 0px 0px 40px;">
              <li>la llave unica es REFKEY, por lo tanto:</li>
              <ul>
                <li>Si llega un nuevo REFKEY inserto</li>
                <li>Si llega un REFKEY que ya tengo updateo</li>
              </ul>
            </ul>
            """;

        var markdown = TicketHtml.ToMarkdown(Html);

        Assert.Contains("- la llave unica es REFKEY, por lo tanto:", markdown, StringComparison.Ordinal);
        Assert.Contains("  - Si llega un nuevo REFKEY inserto", markdown, StringComparison.Ordinal);
        Assert.Contains("  - Si llega un REFKEY que ya tengo updateo", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordered_list_numbers_itself()
    {
        var markdown = TicketHtml.ToMarkdown("<ol><li>uno</li><li>dos</li><li>tres</li></ol>");

        Assert.Contains("1. uno", markdown, StringComparison.Ordinal);
        Assert.Contains("2. dos", markdown, StringComparison.Ordinal);
        Assert.Contains("3. tres", markdown, StringComparison.Ordinal);
    }

    // ---------- inline marks ----------

    [Fact]
    public void Bold_and_italic_survive_and_styling_only_markup_does_not()
    {
        // <span style="background-color:rgb(0,255,0)"> and <u> are in the real input. Neither has a
        // Markdown equivalent worth inventing, and both must keep their text.
        var markdown = TicketHtml.ToMarkdown(
            "<span style=\"background-color:rgb(0, 255, 0);\"><b>Van dos</b><u> observaciones</u></span>");

        Assert.Contains("**Van dos**", markdown, StringComparison.Ordinal);
        Assert.Contains("observaciones", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("background-color", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_becomes_markdown_with_its_text_intact()
    {
        Assert.Equal(
            "[la maqueta](https://example.com/a?b=1)",
            TicketHtml.ToMarkdown("<a href=\"https://example.com/a?b=1\">la maqueta</a>"));
    }

    [Fact]
    public void A_link_with_no_text_is_dropped_rather_than_left_as_an_empty_pair_of_brackets()
    {
        Assert.Equal(string.Empty, TicketHtml.ToMarkdown("<a href=\"https://example.com\"></a>"));
    }

    [Fact]
    public void An_image_keeps_its_source_for_the_mirror_to_rewrite()
    {
        // The source stays verbatim here: only the mirror knows where the attachment was downloaded,
        // and rewriting it here would make this function need a filesystem.
        var markdown = TicketHtml.ToMarkdown(
            "<img src=\"https://dev.azure.com/x/_apis/wit/attachments/abc?fileName=captura.png\" alt=\"captura\">");

        Assert.Contains("![captura](https://dev.azure.com/x/_apis/wit/attachments/abc?fileName=captura.png)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tag_whose_attribute_value_contains_a_closing_bracket_is_still_one_tag()
    {
        // An image URL with a query string is the real case; a naive scan for '>' cuts the tag in half.
        var markdown = TicketHtml.ToMarkdown("<img src=\"https://x/a?q=1&amp;b=&gt;2\" alt=\"x\">texto");

        Assert.Contains("texto", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("src=", markdown, StringComparison.Ordinal);
    }

    // ---------- the div soup ----------

    [Fact]
    public void Sixty_nested_divs_do_not_become_sixty_blank_lines()
    {
        // The measured input has 63 divs and 66 <br> for four paragraphs of prose. Emitting a
        // paragraph break per div close would produce a page of whitespace.
        var html = string.Concat(Enumerable.Repeat("<div style=\"box-sizing:border-box;\">", 20))
            + "contenido"
            + string.Concat(Enumerable.Repeat("</div>", 20));

        var markdown = TicketHtml.ToMarkdown(html);

        Assert.Equal("contenido", markdown);
    }

    [Fact]
    public void Nothing_in_nothing_out()
    {
        Assert.Equal(string.Empty, TicketHtml.ToMarkdown(null));
        Assert.Equal(string.Empty, TicketHtml.ToMarkdown("   "));
    }

    [Fact]
    public void Unbalanced_markup_does_not_throw()
    {
        // Forgiving on purpose: a ticket that fails to render is worse than one that renders plainly.
        Assert.NotNull(TicketHtml.ToMarkdown("<div><ul><li>a</div></li></ul></ul><b>c"));
        Assert.NotNull(TicketHtml.ToMarkdown("<a href=\"x\">sin cerrar"));
        Assert.NotNull(TicketHtml.ToMarkdown("<div"));
    }

    [Fact]
    public void A_real_description_converts_to_readable_markdown()
    {
        // Trimmed from a live work item, keeping every construct it actually used.
        const string Html = """
            <div><span style="background-color:rgb(0, 255, 0);"><b><u>Van dos observaciones.</u></b></span><br><br>
            <div style="box-sizing:border-box;">1)<b> Sobre la l&oacute;gica de procesamiento</b>: cada avro
            saca todos los registros de hoy y ayer.&nbsp; </div><div style="box-sizing:border-box;"><br> </div>
            <div style="box-sizing:border-box;"><ul style="box-sizing:border-box;"><li>la llave <u>unica </u>es REFKEY:</li>
            <ul><li>Si llega un nuevo REFKEY inserto </li><li>Si llega un REFKEY que ya tengo updateo </li></ul></ul></div></div>
            """;

        var markdown = TicketHtml.ToMarkdown(Html);

        Assert.Contains("**Van dos observaciones.**", markdown, StringComparison.Ordinal);
        Assert.Contains("**Sobre la lógica de procesamiento**", markdown, StringComparison.Ordinal);
        Assert.Contains("- la llave unica es REFKEY:", markdown, StringComparison.Ordinal);
        Assert.Contains("  - Si llega un nuevo REFKEY inserto", markdown, StringComparison.Ordinal);

        // No markup survives, and no run of blank lines either.
        Assert.DoesNotContain("<", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("&nbsp;", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", markdown, StringComparison.Ordinal);
    }
}
