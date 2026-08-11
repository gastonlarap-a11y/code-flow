using System.Text.RegularExpressions;

namespace CodeFlow.Tickets;

/// <summary>A ticket a branch name appears to be about.</summary>
/// <param name="Provider">
/// <c>azure</c> or <c>jira</c>. Naming the provider is the point of returning a record rather than a
/// bare id: a branch called <c>feature/PROJ-45-login</c> is recognisably <em>not</em> an Azure work
/// item, and saying so lets the caller explain that Jira is not connected instead of looking up a
/// work item numbered 45 and finding somebody else's.
/// </param>
internal readonly record struct TicketRef(string Provider, string ExternalId);

/// <summary>
/// Guesses which ticket a branch is work for, from its name alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>A suggestion, never an answer.</b> An explicit row in <c>ticket_links</c> always wins, and the
/// UI shows what this guessed so it can be corrected before anything is linked. That framing is what
/// makes the heuristic affordable: it is allowed to be wrong, so it can be useful on the common
/// naming conventions instead of being right on none of them.
/// </para>
/// <para>
/// Pure and without IO so it can be tested against a list of real branch names, which is the only
/// way to tell a useful pattern from one that fires on <c>release/2025-cleanup</c>.
/// </para>
/// </remarks>
internal static partial class TicketBranchRef
{
    /// <summary>Azure DevOps' own smart-reference syntax, as used in commit messages.</summary>
    /// <remarks>
    /// Checked first and matched anywhere in the name because it is the one form that is
    /// unambiguous: nobody writes <c>AB#</c> by accident.
    /// </remarks>
    [GeneratedRegex(@"\bAB#(\d{1,9})\b", RegexOptions.IgnoreCase)]
    private static partial Regex AzureSmartReference();

    /// <summary>
    /// A Jira issue key.
    /// </summary>
    /// <remarks>
    /// <b>Upper case is required, and that is the whole safeguard.</b> Jira keys are written
    /// upper case by every tool that emits them, while accepting lower case would match
    /// <c>utf-8</c> in <c>feature/utf-8-encoding</c> and <c>v2-3</c> in a version branch. The cost
    /// is missing a hand-typed <c>feature/proj-45</c>; the alternative is firing on branches that
    /// have nothing to do with a ticket, which is worse when the result is offered as a default.
    /// </remarks>
    [GeneratedRegex(@"\b([A-Z][A-Z0-9]+)-(\d{1,9})\b")]
    private static partial Regex JiraKey();

    /// <summary>A leading work-item number on the branch's own segment.</summary>
    /// <remarks>
    /// Anchored at the start of the last path segment rather than searched for anywhere: a number
    /// in the middle of a name is far more often a version, a date or a count than a ticket.
    /// </remarks>
    [GeneratedRegex(@"^(\d{1,9})(?:[-_.]|$)")]
    private static partial Regex LeadingNumber();

    /// <summary>
    /// The ticket <paramref name="branch"/> looks like it belongs to, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b>Known false positive, kept deliberately</b>: a date-led branch such as
    /// <c>release/2025-cleanup</c> resolves to work item 2025. Nothing in the name distinguishes a
    /// year from a work-item number, and refusing every four-digit id would reject the far more
    /// common real case. It surfaces as a suggestion the user can reject, which is the reason this
    /// trade is acceptable rather than a defect to be fixed here.
    /// </remarks>
    public static TicketRef? Detect(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        var name = branch.Trim();
        if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            name = name["refs/heads/".Length..];
        }

        if (AzureSmartReference().Match(name) is { Success: true } smart)
        {
            return new TicketRef("azure", smart.Groups[1].Value);
        }

        if (JiraKey().Match(name) is { Success: true } jira)
        {
            return new TicketRef("jira", jira.Value);
        }

        // The last segment only: `feature/1234-login` is about 1234, and a prefix like
        // `users/gaston/1234-login` should not be read as a ticket called `gaston`.
        var segment = name[(name.LastIndexOf('/') + 1)..];

        return LeadingNumber().Match(segment) is { Success: true } numbered
            ? new TicketRef("azure", numbered.Groups[1].Value)
            : null;
    }
}
