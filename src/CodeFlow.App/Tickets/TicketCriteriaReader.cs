using System.Text.Json;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Tickets;

/// <summary>
/// Finds what a ticket actually asks for, among the fields that might hold it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because the obvious answer is wrong.</b> Azure has a field named
/// <c>Microsoft.VSTS.Common.AcceptanceCriteria</c>, and reading it looked like the whole job.
/// Measured against a live organisation across three real backlog items, it held
/// <c>&lt;div&gt;&lt;b&gt;-&lt;/b&gt; &lt;/div&gt;</c> — a hyphen — while the requirements sat in
/// <c>System.Description</c>. The field exists on 8 of that process's 33 work item types and is
/// filled on none of the ones checked.
/// </para>
/// <para>
/// <b>And the second obvious answer is also wrong.</b> That process defines sixteen
/// <c>Custom.*</c> fields with names like <c>Funcionamiento</c> and <c>Testing</c>, which read like
/// a requirements template. They are: every one held identical text across all three tickets,
/// because they are the questions on the refinement form, not the answers. Concatenating them would
/// hand the model two thousand characters of unanswered questionnaire, and every criterion would
/// come back unmet in a way that looks like a defect in the code.
/// </para>
/// </remarks>
internal static class TicketCriteriaReader
{
    /// <summary>Below this many characters of real text, a field is a placeholder.</summary>
    /// <remarks>
    /// Measured on tag-stripped content, never on the raw string:
    /// <c>&lt;div&gt;&lt;b&gt;-&lt;/b&gt; &lt;/div&gt;</c> is twenty characters of markup and one of
    /// content, and counting the former calls an empty box a requirement.
    /// </remarks>
    internal const int MinimumSubstance = 25;

    /// <summary>The fields consulted, in order, when a project has not chosen its own.</summary>
    /// <remarks>
    /// Acceptance criteria first because that is where it belongs when a team fills it in;
    /// description second because that is where it actually is. <c>Custom.*</c> fields are
    /// deliberately absent — see this type's own remarks.
    /// </remarks>
    internal static readonly string[] DefaultFields =
    [
        "Microsoft.VSTS.Common.AcceptanceCriteria",
        "System.Description",
    ];

    public const string ModeList = "list";

    public const string ModeProse = "prose";

    public const string ModeNone = "none";

    /// <summary>The setting a project overrides its field order with.</summary>
    /// <remarks>
    /// Keyed by organisation and board project rather than by the app's own project id, because the
    /// convention belongs to the board: every repository pointed at one board should read its
    /// tickets the same way.
    /// </remarks>
    public static string SettingKey(string org, string project) => $"ticket_criteria_fields:{org}:{project}";

    /// <summary>The field order this board uses.</summary>
    public static IReadOnlyList<string> FieldsFor(SqliteConnection connection, string org, string project)
    {
        var configured = Settings.GetSetting(connection, SettingKey(org, project));
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultFields;
        }

        var fields = configured
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // An explicitly emptied setting means "the defaults", not "no criteria at all". Nobody
        // clears this field to make the review stop reading the ticket; they clear it to start over.
        return fields.Length > 0 ? fields : DefaultFields;
    }

    /// <summary>
    /// Reads the criteria out of a work item's fields.
    /// </summary>
    /// <param name="others">
    /// Raw payloads of other cached tickets of the same type, used to recognise an unanswered
    /// template. Pass an empty list when there are none — with fewer than two tickets the comparison
    /// cannot say anything, and it then excludes nothing rather than guessing.
    /// </param>
    public static TicketCriteria Read(
        JsonElement fields, IReadOnlyList<string> order, IReadOnlyList<string> others)
    {
        foreach (var field in order)
        {
            if (!fields.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var html = value.GetString();
            if (TicketHtml.SubstanceLength(html) < MinimumSubstance)
            {
                continue;
            }

            if (IsTemplate(field, html, others))
            {
                continue;
            }

            var markdown = TicketHtml.ToMarkdown(html);
            var items = Enumerate(markdown);

            return items.Count > 0
                ? new TicketCriteria(ModeList, field, markdown, items)
                : new TicketCriteria(ModeProse, field, markdown, []);
        }

        // Said out loud rather than papered over: a review that cannot find requirements must
        // report findings only, not invent criteria to score against.
        return new TicketCriteria(ModeNone, null, string.Empty, []);
    }

    /// <summary>
    /// Whether a field's content is the process's template rather than this ticket's answer.
    /// </summary>
    /// <remarks>
    /// The signal is identity across work items: a requirement is written per ticket, a form is not.
    /// Compared on tag-stripped text so the editor's own markup churn does not defeat the match.
    /// </remarks>
    private static bool IsTemplate(string field, string? html, IReadOnlyList<string> others)
    {
        if (others.Count == 0)
        {
            return false;
        }

        var mine = TicketHtml.PlainText(html);

        foreach (var other in others)
        {
            try
            {
                using var document = JsonDocument.Parse(other);
                if (document.RootElement.TryGetProperty("fields", out var otherFields)
                    && otherFields.TryGetProperty(field, out var otherValue)
                    && otherValue.ValueKind == JsonValueKind.String
                    && string.Equals(TicketHtml.PlainText(otherValue.GetString()), mine, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // A cached payload that will not parse says nothing about this field either way.
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a Markdown list into numbered criteria, or answers that there is no list.
    /// </summary>
    /// <remarks>
    /// Only a real list counts. Numbering prose by sentence or by line produces criteria cut through
    /// the middle of a rule, and a model asked to judge those reports failures that belong to the
    /// splitting rather than to the code — which is worse than admitting the ticket is narrative and
    /// letting the model enumerate it.
    /// </remarks>
    private static List<string> Enumerate(string markdown)
    {
        var items = new List<string>();

        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();

            // A nested bullet continues the criterion above it rather than starting a new one: it
            // is a sub-case, and promoting it would double-count the rule it qualifies.
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (items.Count > 0 && trimmed.Length > 0)
                {
                    items[^1] = $"{items[^1]} {StripMarker(trimmed)}";
                }

                continue;
            }

            if (StartsList(trimmed) && StripMarker(trimmed) is { Length: > 0 } text)
            {
                items.Add(text);
            }
        }

        return items;
    }

    private static bool StartsList(string line) =>
        line.StartsWith("- ", StringComparison.Ordinal)
        || (line.Length > 2 && char.IsAsciiDigit(line[0]) && line.AsSpan(1).StartsWith(". ", StringComparison.Ordinal));

    private static string StripMarker(string line) =>
        line.StartsWith("- ", StringComparison.Ordinal)
            ? line[2..].Trim()
            : line.IndexOf(". ", StringComparison.Ordinal) is var dot && dot is > 0 and < 4
                ? line[(dot + 2)..].Trim()
                : line.Trim();
}
