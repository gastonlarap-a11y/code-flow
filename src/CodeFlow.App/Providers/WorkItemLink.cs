using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeFlow.Providers;

/// <summary>
/// A work item somebody pasted or typed, with as much of its address as the text carried.
/// </summary>
/// <param name="Org">The organisation, or <see langword="null"/> when the text was just an id.</param>
/// <param name="Project">
/// The project, or <see langword="null"/>. Absent from an organisation-scoped link
/// (<c>/{org}/_workitems/edit/{id}</c>) as well as from a bare id, and the caller fills it in from the
/// workspace's own configuration — which it has to be able to do anyway, since typing <c>426647</c>
/// is the fastest way to link a ticket you already know.
/// </param>
public sealed record WorkItemRef(long Id, string? Org, string? Project);

/// <summary>
/// Recognises the several shapes a work item's address arrives in.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="PrLink"/> and deliberately not built on it: <c>PrLink.Split</c> discards
/// the query string, and a work item's id lives in the query on every board and taskboard URL —
/// <c>…/_boards/board/t/Team/Stories/?workitem=426647</c>. Reusing that splitter would silently fail
/// on the URL a user is most likely to have in their clipboard, because it is the page they were
/// looking at when they decided to link the branch.
/// </para>
/// <para>
/// Azure-only today. A Jira key (<c>PROJ-45</c>) is deliberately not matched here — it belongs to a
/// provider that is not connected, and answering with an Azure-shaped reference would send the app
/// looking for a work item numbered 45.
/// </para>
/// </remarks>
public static partial class WorkItemLink
{
    /// <summary>The canonical work-item page: <c>…/{project}/_workitems/edit/{id}</c>.</summary>
    private const string WorkItemsSegment = "_workitems";

    /// <summary>How every board, backlog and taskboard URL carries the selected item.</summary>
    [GeneratedRegex(@"[?&]workitem=(\d{1,9})\b", RegexOptions.IgnoreCase)]
    private static partial Regex WorkItemQuery();

    /// <summary>A bare id, or Azure's own smart-reference syntax.</summary>
    [GeneratedRegex(@"^\s*(?:AB#)?(\d{1,9})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BareId();

    /// <summary>
    /// Parses whatever the user pasted, or returns <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Ordered cheapest-first: a bare id needs no URL parsing at all, and it is what someone types
    /// when they already know the number.
    /// </remarks>
    public static WorkItemRef? Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (BareId().Match(input) is { Success: true } bare)
        {
            return new WorkItemRef(Number(bare.Groups[1].Value), null, null);
        }

        var (host, segments) = SplitPath(input);
        var org = Organisation(host, segments);

        // The board/taskboard/backlog family: the id is in the query, and the path names the
        // project. Checked before the path form because such a URL also contains _boards, not
        // _workitems, so the two never compete.
        if (WorkItemQuery().Match(input) is { Success: true } queried)
        {
            return new WorkItemRef(Number(queried.Groups[1].Value), org, ProjectFrom(host, segments));
        }

        var index = Array.FindIndex(segments, s => s.Equals(WorkItemsSegment, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        // `/_workitems/edit/426647` and `/_workitems/426647` both occur; take the last numeric
        // segment after the marker rather than assuming which one it is.
        var id = segments[(index + 1)..]
            .Select(segment => long.TryParse(segment, CultureInfo.InvariantCulture, out var value) ? value : (long?)null)
            .OfType<long>()
            .Cast<long?>()
            .LastOrDefault();

        if (id is null)
        {
            return null;
        }

        // A project segment only exists when _workitems is not the first one: an organisation-scoped
        // link is `/{org}/_workitems/edit/{id}` on dev.azure.com and `/_workitems/…` on the legacy host.
        var project = index > (IsLegacyHost(host) ? 0 : 1) ? segments[index - 1] : null;

        return new WorkItemRef(id.Value, org, project);
    }

    /// <summary>The organisation a link addresses, from either host form.</summary>
    /// <remarks>
    /// <c>dev.azure.com/{org}/…</c> puts it in the path; <c>{org}.visualstudio.com/…</c> puts it in
    /// the host. Both are live — the legacy host still serves the modern UI, which is why a pasted
    /// link is as likely to carry one as the other.
    /// </remarks>
    private static string? Organisation(string host, string[] segments) =>
        IsLegacyHost(host) ? host[..host.IndexOf('.', StringComparison.Ordinal)]
        : segments.Length > 0 && host.EndsWith("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? segments[0]
        : null;

    /// <summary>The project segment of a board-style URL.</summary>
    private static string? ProjectFrom(string host, string[] segments)
    {
        var offset = IsLegacyHost(host) ? 0 : 1;
        return segments.Length > offset ? segments[offset] : null;
    }

    private static bool IsLegacyHost(string host) =>
        host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits a pasted link into its host and decoded path segments, query discarded.</summary>
    private static (string Host, string[] Segments) SplitPath(string url)
    {
        var cleaned = url.Trim().Split('#')[0].Split('?')[0].TrimEnd('/');

        var withoutScheme = cleaned.StartsWith("https://", StringComparison.Ordinal) ? cleaned[8..]
            : cleaned.StartsWith("http://", StringComparison.Ordinal) ? cleaned[7..]
            : cleaned;

        var slash = withoutScheme.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return (withoutScheme, []);
        }

        var segments = withoutScheme[(slash + 1)..]
            .Split('/')
            .Where(segment => segment.Length > 0)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        return (withoutScheme[..slash], segments);
    }

    private static long Number(string digits) => long.Parse(digits, CultureInfo.InvariantCulture);
}
