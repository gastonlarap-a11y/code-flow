using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CodeFlow.Providers;
using CodeFlow.Providers.Azure;
using CodeFlow.Storage;

namespace CodeFlow.Tickets;

/// <summary>
/// Publishing a review verdict onto the work item it judged.
/// </summary>
/// <remarks>
/// <para>
/// The only thing in this feature that writes to a board, and it writes on a button press with the
/// text already on screen — never as a side effect of a review finishing. A review is run many times
/// while work is in progress, and a board that collects every one of those attempts is worse than a
/// board with nothing on it (<c>WI-022</c>).
/// </para>
/// <para>
/// The body travels from the renderer rather than being rebuilt here from the stored run. That is
/// the point of the button: what a person read and approved is what lands, with no room for this to
/// derive something subtly different from what they were shown.
/// </para>
/// </remarks>
internal static partial class TicketComment
{
    /// <summary>Posts a comment and returns the work item's web URL.</summary>
    /// <exception cref="ProviderException">
    /// The ticket is unknown, its provider is not one that can be commented on, or Azure refused.
    /// </exception>
    public static async Task<string> PostAsync(
        Database database, HttpClient http, string ticketId, string body, CancellationToken cancellationToken)
    {
        var ticket = await database
            .ReadAsync(connection => TicketStore.Get(connection, ticketId), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ProviderException("That ticket is not in this workspace any more");

        // Only Azure has a work item to comment on. Stated rather than assumed because `Ticket`
        // carries a provider column precisely so a second board can exist later.
        if (!string.Equals(ticket.Provider, "azure", StringComparison.Ordinal))
        {
            throw new ProviderException($"Commenting is not supported for {ticket.Provider} tickets");
        }

        if (!long.TryParse(ticket.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var workItemId))
        {
            throw new ProviderException($"'{ticket.ExternalId}' is not a work item number");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ProviderException("There is nothing to publish");
        }

        // The same lookup every read in this feature makes, so a refused or missing PAT is reported
        // here exactly as it is when syncing — one credential story, not two.
        var pat = PullRequestHosts.PatForOrg(ticket.Org);

        await AzureWorkItemClient.AddCommentAsync(
            http, ticket.Org, ticket.Project, workItemId, ToHtml(body), pat, cancellationToken)
            .ConfigureAwait(false);

        return ticket.WebUrl;
    }

    /// <summary>
    /// Renders the verdict's markdown as the HTML an Azure comment displays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Azure work item comments are rich text, not markdown: posting the verdict as written puts
    /// <c>## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN</c> and <c>**cumple**</c> on the board with
    /// their punctuation showing. This is the inverse of <see cref="TicketHtml"/>, and deliberately
    /// only covers what the verdict can contain — the two headings, bold, inline code and the
    /// bullet, all of which are fixed by <c>XLANG-016</c> and <c>XLANG-001</c>. Anything outside
    /// that subset survives as its own escaped text rather than being guessed at.
    /// </para>
    /// <para>
    /// Escaping happens <b>first</b>, so a diff quoted inside the verdict cannot close a tag. Every
    /// tag this emits is added after that, from patterns matched on the escaped text.
    /// </para>
    /// </remarks>
    internal static string ToHtml(string markdown)
    {
        var html = new StringBuilder();

        foreach (var rawLine in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = Escape(rawLine.TrimEnd());

            if (line.Length == 0)
            {
                continue;
            }

            line = InlineCodePattern().Replace(line, "<code>$1</code>");
            line = BoldPattern().Replace(line, "<b>$1</b>");

            html.Append(Wrap(line));
        }

        return html.ToString();
    }

    /// <summary>Neutralises the three characters that are HTML syntax in text content.</summary>
    /// <remarks>
    /// Hand-written rather than <see cref="WebUtility.HtmlEncode"/>, which also escapes every
    /// non-ASCII character to a numeric entity. This app writes Spanish and the verdict carries
    /// emoji, so that would render correctly and read as <c>An&amp;#225;lisis</c> to anyone who
    /// looks at the comment through the API — for no gain, since the payload is UTF-8 JSON.
    /// Ampersand goes first, or it would escape the escapes.
    /// </remarks>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>Puts one already-escaped line inside the element its prefix asks for.</summary>
    /// <remarks>
    /// <c>h3</c> and <c>h4</c> rather than <c>h1</c>/<c>h2</c>: a comment is nested inside the work
    /// item's own page, and a heading that outranks the item's title reads as a mistake.
    /// </remarks>
    private static string Wrap(string line) => line switch
    {
        _ when line.StartsWith("### ", StringComparison.Ordinal) => $"<h4>{line[4..]}</h4>",
        _ when line.StartsWith("## ", StringComparison.Ordinal) => $"<h3>{line[3..]}</h3>",
        _ when line.StartsWith("- ", StringComparison.Ordinal) => $"<div>• {line[2..]}</div>",
        // A horizontal rule is the separator before the footer, and `<hr>` is what it means.
        "---" => "<hr>",
        _ => $"<div>{line}</div>",
    };

    /// <remarks>
    /// Non-greedy and single-line: two bold runs on one line must not merge into one that swallows
    /// what is between them.
    /// </remarks>
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodePattern();
}
