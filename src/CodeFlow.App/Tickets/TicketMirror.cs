using System.Text;

namespace CodeFlow.Tickets;

/// <summary>
/// One downloaded attachment, ready to be written beside the ticket.
/// </summary>
internal sealed record TicketAttachment(string FileName, byte[] Content, string SourceUrl);

/// <summary>
/// Writes a ticket's readable copy to disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mirror is a derived artefact and <c>notes/</c> is not.</b> Four names are rewritten on
/// every sync — <c>ticket.md</c>, <c>acceptance-criteria.md</c>, <c>raw.json</c> and
/// <c>attachments/</c> — and this type knows no others. That is the whole protection: there is no
/// recursive delete anywhere here, so anything else in the directory survives by construction
/// rather than by a rule someone has to remember. It is the discipline <c>WS-007</c> already
/// applies to skill paths.
/// </para>
/// <para>
/// Synchronous <see cref="System.IO"/> on purpose, called through <c>Task.Run</c> by the async
/// caller — the same shape <c>SkillSync</c> uses, for the same reason.
/// </para>
/// </remarks>
internal static class TicketMirror
{
    public const string TicketFile = "ticket.md";

    public const string CriteriaFile = "acceptance-criteria.md";

    public const string RawFile = "raw.json";

    public const string AttachmentsDirectory = "attachments";

    /// <summary>The user's own space. Created once, never written to and never cleaned.</summary>
    public const string NotesDirectory = "notes";

    /// <summary>
    /// Reads back whatever the user wrote in <c>notes/</c>, so a review can be told about it.
    /// </summary>
    /// <remarks>
    /// The one direction this type ever moves data out of that directory, and it is the reason the
    /// directory exists: what a ticket leaves unsaid — the decision taken in a meeting, the field
    /// nobody filled in — is exactly what a review judging "does this deliver the ticket" is missing.
    /// Reading is not writing: <c>WI-003</c> still holds, nothing here creates, deletes or modifies.
    /// <para>
    /// Plain text only, by extension, and a file that will not read is skipped rather than failing
    /// the review. Returns the empty string when the directory does not exist, which is the normal
    /// case: it is created empty and most tickets never get a note.
    /// </para>
    /// </remarks>
    /// <param name="budgetChars">Read no further than this, so one long note cannot crowd out the diff.</param>
    public static string ReadNotes(string directory, int budgetChars)
    {
        var root = Path.Combine(directory, NotesDirectory);
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (var file in Directory.EnumerateFiles(root).Order(StringComparer.Ordinal))
        {
            if (Path.GetExtension(file) is not (".md" or ".txt" or ".markdown"))
            {
                continue;
            }

            try
            {
                text.Append("### ").Append(Path.GetFileName(file)).Append('\n')
                    .Append(File.ReadAllText(file, Encoding.UTF8)).Append("\n\n");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A note nobody can read is not a reason to refuse the review.
            }

            if (text.Length >= budgetChars)
            {
                break;
            }
        }

        return text.Length <= budgetChars ? text.ToString() : text.ToString(0, budgetChars);
    }

    /// <summary>Writes every derived file, leaving everything else in the directory alone.</summary>
    public static void Write(
        string directory,
        Ticket ticket,
        TicketCriteria criteria,
        string rawJson,
        IReadOnlyList<TicketAttachment> attachments,
        IReadOnlyList<string> skipped)
    {
        Directory.CreateDirectory(directory);

        // Created empty on the first sync so there is an obvious place to put your own notes, and
        // never touched again — not even to check what is in it.
        Directory.CreateDirectory(Path.Combine(directory, NotesDirectory));

        var saved = WriteAttachments(directory, attachments);

        File.WriteAllText(Path.Combine(directory, RawFile), rawJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, CriteriaFile), RenderCriteria(ticket, criteria), Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, TicketFile), RenderTicket(ticket, criteria, saved, skipped), Encoding.UTF8);
    }

    /// <summary>
    /// Saves the attachments and reports where each source URL ended up.
    /// </summary>
    /// <remarks>
    /// The directory is emptied first so a renamed or removed attachment does not linger, and it is
    /// emptied file by file rather than by deleting the directory: a recursive delete one path
    /// segment wrong is the failure this whole type is shaped to avoid.
    /// </remarks>
    private static Dictionary<string, string> WriteAttachments(
        string directory, IReadOnlyList<TicketAttachment> attachments)
    {
        var root = Path.Combine(directory, AttachmentsDirectory);
        Directory.CreateDirectory(root);

        foreach (var existing in Directory.EnumerateFiles(root))
        {
            File.Delete(existing);
        }

        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var attachment in attachments)
        {
            var name = UniqueName(attachment.FileName, used);
            File.WriteAllBytes(Path.Combine(root, name), attachment.Content);
            saved[attachment.SourceUrl] = $"{AttachmentsDirectory}/{name}";
        }

        return saved;
    }

    /// <summary>
    /// A file name that is safe on both platforms and not already taken.
    /// </summary>
    /// <remarks>
    /// Two attachments on one work item can share a name — Azure keys them by GUID, not by file
    /// name — and letting the second overwrite the first would lose evidence silently.
    /// </remarks>
    private static string UniqueName(string fileName, HashSet<string> used)
    {
        var stem = TicketPaths.Slug(Path.GetFileNameWithoutExtension(fileName));
        var extension = TicketPaths.Slug(Path.GetExtension(fileName).TrimStart('.'));

        var name = stem.Length == 0 ? "adjunto" : stem;
        if (extension.Length > 0)
        {
            name = $"{name}.{extension}";
        }

        var candidate = name;
        var counter = 2;
        while (!used.Add(candidate))
        {
            candidate = extension.Length > 0
                ? $"{Path.GetFileNameWithoutExtension(name)}-{counter}.{extension}"
                : $"{name}-{counter}";
            counter++;
        }

        return candidate;
    }

    /// <summary>The ticket as a person reads it, and as the review prompt is handed it.</summary>
    private static string RenderTicket(
        Ticket ticket,
        TicketCriteria criteria,
        IReadOnlyDictionary<string, string> attachments,
        IReadOnlyList<string> skipped)
    {
        var page = new StringBuilder();

        page.Append("# ").Append(ticket.ExternalId).Append(" · ").AppendLine(ticket.Title);
        page.AppendLine();
        page.Append("- **Tipo:** ").AppendLine(ticket.WorkItemType);
        page.Append("- **Estado:** ").AppendLine(ticket.State);
        page.Append("- **Asignado a:** ").AppendLine(ticket.AssignedTo ?? "(sin asignar)");
        page.Append("- **Organización:** ").Append(ticket.Org).Append(" / ").AppendLine(ticket.Project);
        page.Append("- **Enlace:** ").AppendLine(ticket.WebUrl);
        page.Append("- **Sincronizado:** ").AppendLine(ticket.SyncedAt);
        page.AppendLine();

        page.AppendLine("## Qué pide el ticket");
        page.AppendLine();
        page.AppendLine(criteria.Mode == TicketCriteriaReader.ModeNone
            // Stated rather than left blank: an empty section reads as a rendering failure, and the
            // fact that no field carried requirements is itself the thing worth knowing.
            ? "_Ningún campo del ticket contiene criterios con contenido suficiente._"
            : Relink(criteria.Markdown, attachments));

        if (skipped.Count > 0)
        {
            page.AppendLine();
            page.AppendLine("## Adjuntos no descargados");
            page.AppendLine();
            foreach (var reason in skipped)
            {
                page.Append("- ").AppendLine(reason);
            }
        }

        page.AppendLine();
        page.AppendLine("---");
        page.AppendLine();
        page.AppendLine(
            $"_Copia generada por CodeFlow. Los archivos de este directorio se reescriben en cada "
            + $"sincronización, salvo `{NotesDirectory}/`, que es tuyo._");

        return page.ToString();
    }

    private static string RenderCriteria(Ticket ticket, TicketCriteria criteria)
    {
        var page = new StringBuilder();
        page.Append("# Criterios · ").Append(ticket.ExternalId).Append(' ').AppendLine(ticket.Title);
        page.AppendLine();

        switch (criteria.Mode)
        {
            case TicketCriteriaReader.ModeList:
                page.Append("_Fuente: `").Append(criteria.Field).AppendLine("` — lista explícita._");
                page.AppendLine();
                for (var i = 0; i < criteria.Items.Count; i++)
                {
                    page.Append("- **AC-").Append(i + 1).Append(":** ").AppendLine(criteria.Items[i]);
                }

                break;

            case TicketCriteriaReader.ModeProse:
                page.Append("_Fuente: `").Append(criteria.Field)
                    .AppendLine("` — redacción en prosa, sin lista que numerar._");
                page.AppendLine();
                page.AppendLine(criteria.Markdown);
                break;

            default:
                page.AppendLine("_Este ticket no declara criterios de aceptación con contenido._");
                break;
        }

        return page.ToString();
    }

    /// <summary>Points every image at the copy that was downloaded next to it.</summary>
    /// <remarks>
    /// An attachment URL needs the PAT to fetch, so an untouched <c>src</c> renders as a broken
    /// image for a person and as nothing at all for the model — losing evidence the ticket does
    /// carry. A source that was not downloaded keeps its URL rather than pointing at a file that
    /// is not there.
    /// </remarks>
    private static string Relink(string markdown, IReadOnlyDictionary<string, string> attachments)
    {
        foreach (var (url, relative) in attachments)
        {
            markdown = markdown.Replace(url, relative, StringComparison.Ordinal);
        }

        return markdown;
    }
}
