using System.Net;
using System.Text;

namespace CodeFlow.Tickets;

/// <summary>
/// Turns Azure Boards' rich-text HTML into the Markdown a ticket's mirror is written in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-rolled rather than a dependency, and the measurement is why.</b> A real work item from a
/// live organisation uses eight distinct tags: <c>br</c>, <c>div</c>, <c>li</c>, <c>ul</c>,
/// <c>span</c>, <c>u</c>, <c>img</c>, <c>b</c>. That is a bounded vocabulary, and adding a NuGet
/// package for it would mean a new entry in <c>Directory.Packages.props</c>, two regenerated lock
/// files and a new row in the supply-chain audit — for a conversion whose whole input is one
/// editor's output. The codebase already hand-rolls bounded parsers for the same reason
/// (<c>UnifiedPatch</c>, the percent-encoder).
/// </para>
/// <para>
/// It is deliberately forgiving. This is not a validating parser: unknown tags are dropped and their
/// text kept, unbalanced markup does not throw, and the worst outcome is prose that reads a little
/// flat. A ticket that fails to render is worse than one that renders plainly.
/// </para>
/// <para>
/// Entity decoding goes through <see cref="WebUtility.HtmlDecode"/>, which is not culture-sensitive
/// and so is unaffected by this project's <c>InvariantGlobalization</c>. That matters: the real input
/// is full of <c>&amp;nbsp;</c>.
/// </para>
/// </remarks>
internal static class TicketHtml
{
    /// <summary>How deep a nested list may be indented before it stops helping.</summary>
    private const int MaxListDepth = 6;

    /// <summary>
    /// The Markdown rendering of a rich-text field.
    /// </summary>
    /// <remarks>
    /// Image sources are emitted verbatim. Rewriting them to the downloaded copy is the mirror's job,
    /// which is the only part of the app that knows where attachments were saved — and doing it here
    /// would make this function need a filesystem.
    /// </remarks>
    public static string ToMarkdown(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var output = new StringBuilder(html.Length);
        var lists = new Stack<ListLevel>();
        var inline = new Stack<InlineMark>();

        // Where the text of the current <a> began, so the closing tag can wrap what it produced.
        var linkStart = -1;
        string? linkHref = null;

        foreach (var token in Tokenize(html))
        {
            switch (token)
            {
                case TextToken text:
                    Append(output, WebUtility.HtmlDecode(text.Value));
                    break;

                case TagToken tag:
                    Render(tag, output, lists, inline, ref linkStart, ref linkHref);
                    break;
            }
        }

        return Tidy(output.ToString());
    }

    /// <summary>
    /// The field's text with every tag and entity resolved away.
    /// </summary>
    /// <remarks>
    /// <b>The measurement that decides whether a field is a requirement or an empty box.</b> A real
    /// acceptance-criteria field measured against a live organisation holds
    /// <c>&lt;div&gt;&lt;b&gt;-&lt;/b&gt; &lt;/div&gt;</c> — nineteen characters of HTML and one of
    /// content. Counting the raw string would call that a filled-in field and hand the AI a hyphen to
    /// review against.
    /// </remarks>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var output = new StringBuilder(html.Length);
        foreach (var token in Tokenize(html))
        {
            if (token is TextToken text)
            {
                Append(output, WebUtility.HtmlDecode(text.Value));
            }
            else if (token is TagToken { Name: "br" or "p" or "div" or "li" or "tr" })
            {
                Append(output, " ");
            }
        }

        return CollapseSpaces(output.ToString()).Trim();
    }

    /// <summary>How much real content a field carries, tags and entities excluded.</summary>
    public static int SubstanceLength(string? html) => PlainText(html).Length;

    /// <summary>
    /// Opens or closes an inline mark, moving any whitespace outside the markers.
    /// </summary>
    /// <remarks>
    /// <b>The whitespace move is the whole reason this is not two <c>Append</c> calls.</b> The real
    /// input writes <c>1)&lt;b&gt; Sobre la lógica&lt;/b&gt;</c>, with the space inside the tag.
    /// Emitting that literally gives <c>** Sobre la lógica**</c>, which every Markdown renderer shows
    /// as two asterisks followed by plain text — the emphasis silently disappears. Markers have to
    /// hug their text.
    /// </remarks>
    private static void Emphasis(TagToken tag, StringBuilder output, Stack<InlineMark> inline, string marker)
    {
        if (!tag.Closing)
        {
            inline.Push(new InlineMark(marker, output.Length));
            return;
        }

        // A close with no open is malformed input, and dropping it beats emitting a stray marker.
        if (inline.Count == 0)
        {
            return;
        }

        var mark = inline.Pop();
        if (mark.Start > output.Length)
        {
            return;
        }

        var text = output.ToString(mark.Start, output.Length - mark.Start);
        output.Length = mark.Start;

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (char.IsWhiteSpace(text[0]) && output.Length > 0 && output[^1] is not (' ' or '\n'))
        {
            output.Append(' ');
        }

        output.Append(mark.Marker).Append(trimmed).Append(mark.Marker);

        if (char.IsWhiteSpace(text[^1]))
        {
            output.Append(' ');
        }
    }

    private static void Render(
        TagToken tag, StringBuilder output, Stack<ListLevel> lists, Stack<InlineMark> inline,
        ref int linkStart, ref string? linkHref)
    {
        switch (tag.Name)
        {
            case "br":
                output.Append('\n');
                break;

            // Azure's editor nests div after div for layout; treating each close as a paragraph
            // break would leave a page of blank lines. One newline each, collapsed later.
            case "div" or "tr" when tag.Closing:
            case "p" when tag.Closing:
                output.Append(tag.Name == "p" ? "\n\n" : "\n");
                break;

            case "ul" or "ol":
                if (tag.Closing)
                {
                    if (lists.Count > 0)
                    {
                        lists.Pop();
                    }

                    output.Append('\n');
                }
                else
                {
                    lists.Push(new ListLevel(tag.Name == "ol"));
                    output.Append('\n');
                }

                break;

            case "li" when !tag.Closing:
                output.Append('\n').Append(Bullet(lists));
                break;

            case "b" or "strong":
                Emphasis(tag, output, inline, "**");
                break;

            case "i" or "em":
                Emphasis(tag, output, inline, "*");
                break;

            case "code":
                Emphasis(tag, output, inline, "`");
                break;

            case "pre":
                output.Append(tag.Closing ? "\n```\n" : "\n```\n");
                break;

            case "a" when !tag.Closing:
                linkStart = output.Length;
                linkHref = tag.Attribute("href");
                break;

            case "a" when tag.Closing:
                CloseLink(output, ref linkStart, ref linkHref);
                break;

            case "img":
                var source = tag.Attribute("src");
                if (!string.IsNullOrWhiteSpace(source))
                {
                    output.Append("\n![").Append(tag.Attribute("alt") ?? "imagen").Append("](").Append(source).Append(")\n");
                }

                break;

            case "td" or "th" when tag.Closing:
                output.Append(" | ");
                break;

            // `u` has no Markdown equivalent, `span` is only ever styling here, and anything
            // unrecognised keeps its text and loses its markup. All three are the same decision.
            default:
                break;
        }
    }

    /// <summary>Wraps the text an anchor produced, or leaves it alone when there is no target.</summary>
    /// <remarks>
    /// A link whose text is empty is dropped entirely rather than emitted as <c>[](url)</c>, which
    /// renders as nothing while still occupying a line.
    /// </remarks>
    private static void CloseLink(StringBuilder output, ref int linkStart, ref string? linkHref)
    {
        if (linkStart >= 0 && linkStart <= output.Length && !string.IsNullOrWhiteSpace(linkHref))
        {
            var text = output.ToString(linkStart, output.Length - linkStart).Trim();
            output.Length = linkStart;

            if (text.Length > 0)
            {
                output.Append('[').Append(text).Append("](").Append(linkHref).Append(')');
            }
        }

        linkStart = -1;
        linkHref = null;
    }

    /// <summary>The bullet for the current depth, indented by two spaces per enclosing list.</summary>
    private static string Bullet(Stack<ListLevel> lists)
    {
        if (lists.Count == 0)
        {
            return "- ";
        }

        var level = lists.Peek();
        var indent = new string(' ', Math.Min(lists.Count - 1, MaxListDepth) * 2);

        return level.Ordered ? $"{indent}{++level.Counter}. " : $"{indent}- ";
    }

    /// <summary>Appends text, collapsing runs of whitespace that HTML would have collapsed anyway.</summary>
    private static void Append(StringBuilder output, string text)
    {
        foreach (var character in text)
        {
            // A non-breaking space is a space here: it exists in the source for layout, and keeping
            // it produces a character that looks like a space and does not match one.
            var normalised = character is ' ' ? ' ' : character;

            if (normalised is ' ' or '\t' or '\r' or '\n')
            {
                if (output.Length > 0 && output[^1] is not (' ' or '\n'))
                {
                    output.Append(' ');
                }

                continue;
            }

            output.Append(normalised);
        }
    }

    private static string CollapseSpaces(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (output.Length > 0 && output[^1] != ' ')
                {
                    output.Append(' ');
                }

                continue;
            }

            output.Append(character);
        }

        return output.ToString();
    }

    /// <summary>Normalises the blank lines the div soup produced.</summary>
    private static string Tidy(string markdown)
    {
        var lines = markdown.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder(markdown.Length);
        var blanks = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                blanks++;
                continue;
            }

            if (output.Length > 0)
            {
                output.Append(blanks > 0 ? "\n\n" : "\n");
            }

            blanks = 0;
            output.Append(trimmed.TrimStart() is { Length: > 0 } && trimmed.StartsWith(' ') ? trimmed : trimmed.Trim());
        }

        return output.ToString();
    }

    /// <summary>An open inline mark and where its text started.</summary>
    private readonly record struct InlineMark(string Marker, int Start);

    /// <summary>One open list, and how many items it has produced.</summary>
    private sealed class ListLevel(bool ordered)
    {
        public bool Ordered { get; } = ordered;

        public int Counter { get; set; }
    }

    private abstract record Token;

    private sealed record TextToken(string Value) : Token;

    private sealed record TagToken(string Name, bool Closing, IReadOnlyDictionary<string, string> Attributes) : Token
    {
        public string? Attribute(string name) => Attributes.GetValueOrDefault(name);
    }

    /// <summary>
    /// Splits the markup into tags and the text between them.
    /// </summary>
    /// <remarks>
    /// A <c>&lt;</c> that opens nothing recognisable is treated as text, which is what makes a
    /// description containing <c>a &lt; b</c> survive rather than swallowing the rest of the field.
    /// </remarks>
    private static IEnumerable<Token> Tokenize(string html)
    {
        var index = 0;
        while (index < html.Length)
        {
            var open = html.IndexOf('<', index);
            if (open < 0)
            {
                yield return new TextToken(html[index..]);
                yield break;
            }

            if (open > index)
            {
                yield return new TextToken(html[index..open]);
            }

            // A `<` only opens a tag when a name, a slash or a declaration follows it. Without this
            // rule `si a < b entonces</div>` parses as a `<b>` tag running to the next `>`, and the
            // prose turns into stray bold markers — observed, not hypothetical.
            if (open + 1 >= html.Length || !(char.IsAsciiLetter(html[open + 1]) || html[open + 1] is '/' or '!' or '?'))
            {
                yield return new TextToken("<");
                index = open + 1;
                continue;
            }

            var close = FindTagEnd(html, open);
            if (close < 0)
            {
                yield return new TextToken(html[open..]);
                yield break;
            }

            var inner = html[(open + 1)..close].Trim();
            index = close + 1;

            // Comments and processing instructions carry nothing worth keeping.
            if (inner.StartsWith('!') || inner.StartsWith('?'))
            {
                continue;
            }

            var closing = inner.StartsWith('/');
            if (closing)
            {
                inner = inner[1..];
            }

            var nameEnd = inner.AsSpan().IndexOfAny(" \t\r\n/".AsSpan());
            var name = (nameEnd < 0 ? inner : inner[..nameEnd]).ToLowerInvariant();
            if (name.Length == 0)
            {
                continue;
            }

            yield return new TagToken(name, closing, nameEnd < 0 ? new Dictionary<string, string>() : Attributes(inner[nameEnd..]));
        }
    }

    /// <summary>The index of the <c>&gt;</c> that closes a tag, ignoring ones inside attribute values.</summary>
    /// <remarks>
    /// Needed because the real input carries <c>style="box-sizing:border-box;"</c> and, more to the
    /// point, image sources with query strings.
    /// </remarks>
    private static int FindTagEnd(string html, int open)
    {
        var quote = '\0';
        for (var i = open + 1; i < html.Length; i++)
        {
            var character = html[i];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The attributes of a tag, lower-cased by name and unquoted by value.</summary>
    private static Dictionary<string, string> Attributes(string rest)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;

        while (index < rest.Length)
        {
            while (index < rest.Length && (char.IsWhiteSpace(rest[index]) || rest[index] == '/'))
            {
                index++;
            }

            var nameStart = index;
            while (index < rest.Length && rest[index] is not ('=' or '/') && !char.IsWhiteSpace(rest[index]))
            {
                index++;
            }

            if (index == nameStart)
            {
                break;
            }

            var name = rest[nameStart..index].ToLowerInvariant();

            while (index < rest.Length && char.IsWhiteSpace(rest[index]))
            {
                index++;
            }

            if (index >= rest.Length || rest[index] != '=')
            {
                // A valueless attribute, which none of the tags handled here cares about.
                attributes[name] = string.Empty;
                continue;
            }

            index++;
            while (index < rest.Length && char.IsWhiteSpace(rest[index]))
            {
                index++;
            }

            if (index >= rest.Length)
            {
                break;
            }

            string value;
            if (rest[index] is '"' or '\'')
            {
                var quote = rest[index++];
                var valueStart = index;
                while (index < rest.Length && rest[index] != quote)
                {
                    index++;
                }

                value = rest[valueStart..Math.Min(index, rest.Length)];
                index = Math.Min(index + 1, rest.Length);
            }
            else
            {
                var valueStart = index;
                while (index < rest.Length && !char.IsWhiteSpace(rest[index]) && rest[index] != '>')
                {
                    index++;
                }

                value = rest[valueStart..index];
            }

            attributes[name] = WebUtility.HtmlDecode(value);
        }

        return attributes;
    }
}
