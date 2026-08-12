using CodeFlow.Tickets;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// The on-disk copy of a ticket, and the guarantee that it only owns four names.
/// </summary>
public sealed class TicketMirrorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"codeflow-mirror-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Ticket Sample() => new(
        "azure:contoso:Web:426647",
        "azure",
        "contoso",
        "Web",
        "426647",
        "TRANSFORMACIONES - FEEDBACK FLUJO AVRO",
        "Ready to Test",
        "Product Backlog Item",
        "Ada Lovelace",
        "https://dev.azure.com/contoso/Web/_workitems/edit/426647",
        23,
        "unused",
        "2026-08-11T00:00:00.0000000+00:00");

    private static TicketCriteria Prose(string markdown) =>
        new(TicketCriteriaReader.ModeProse, "System.Description", markdown, []);

    private void Write(TicketCriteria criteria, params TicketAttachment[] attachments) =>
        TicketMirror.Write(_directory, Sample(), criteria, """{"id":426647}""", attachments, []);

    // ---------- the guarantee this whole type exists for ----------

    [Fact]
    public void Anything_the_user_put_in_the_directory_survives_a_resync()
    {
        // The mirror is the first thing in the app that writes into a directory a person also uses.
        // It knows four names and no recursive delete, so this holds by construction — but it is
        // the promise made to the user, so it is tested rather than argued.
        Write(Prose("primera versión"));

        var mine = Path.Combine(_directory, "notes", "decisiones.md");
        File.WriteAllText(mine, "lo que decidí");
        var loose = Path.Combine(_directory, "mi-diagrama.excalidraw");
        File.WriteAllText(loose, "{}");

        Write(Prose("segunda versión"));

        Assert.Equal("lo que decidí", File.ReadAllText(mine));
        Assert.Equal("{}", File.ReadAllText(loose));
        Assert.Contains("segunda versión", File.ReadAllText(Path.Combine(_directory, "ticket.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_notes_directory_is_created_once_and_never_filled()
    {
        Write(Prose("cualquier cosa"));

        var notes = Path.Combine(_directory, "notes");
        Assert.True(Directory.Exists(notes));
        Assert.Empty(Directory.EnumerateFileSystemEntries(notes));
    }

    [Fact]
    public void Every_derived_file_is_replaced_not_appended_to()
    {
        Write(Prose("vieja"));
        Write(Prose("nueva"));

        var ticket = File.ReadAllText(Path.Combine(_directory, "ticket.md"));
        Assert.Contains("nueva", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("vieja", ticket, StringComparison.Ordinal);
    }

    // ---------- what the files say ----------

    [Fact]
    public void The_ticket_page_carries_what_a_person_needs_to_recognise_it()
    {
        Write(Prose("el detalle"));

        var page = File.ReadAllText(Path.Combine(_directory, "ticket.md"));

        Assert.Contains("426647", page, StringComparison.Ordinal);
        Assert.Contains("Ready to Test", page, StringComparison.Ordinal);
        Assert.Contains("Product Backlog Item", page, StringComparison.Ordinal);
        Assert.Contains("Ada Lovelace", page, StringComparison.Ordinal);
        Assert.Contains("https://dev.azure.com/contoso/Web/_workitems/edit/426647", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ticket_with_no_criteria_says_so_instead_of_leaving_a_blank_section()
    {
        // An empty section reads as a rendering fault. That no field carried requirements is itself
        // the thing worth knowing, and it is what the review will report.
        TicketMirror.Write(
            _directory, Sample(), new TicketCriteria(TicketCriteriaReader.ModeNone, null, "", []),
            "{}", [], []);

        Assert.Contains(
            "Ningún campo del ticket contiene criterios",
            File.ReadAllText(Path.Combine(_directory, "ticket.md")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_list_is_written_as_numbered_criteria()
    {
        TicketMirror.Write(
            _directory, Sample(),
            new TicketCriteria(TicketCriteriaReader.ModeList, "Microsoft.VSTS.Common.AcceptanceCriteria",
                "- uno\n- dos", ["uno", "dos"]),
            "{}", [], []);

        var criteria = File.ReadAllText(Path.Combine(_directory, "acceptance-criteria.md"));

        Assert.Contains("**AC-1:** uno", criteria, StringComparison.Ordinal);
        Assert.Contains("**AC-2:** dos", criteria, StringComparison.Ordinal);
    }

    [Fact]
    public void Prose_criteria_are_written_whole_and_labelled_as_prose()
    {
        Write(Prose("Van dos observaciones sobre el flujo."));

        var criteria = File.ReadAllText(Path.Combine(_directory, "acceptance-criteria.md"));

        Assert.Contains("prosa", criteria, StringComparison.Ordinal);
        Assert.Contains("Van dos observaciones", criteria, StringComparison.Ordinal);
        Assert.DoesNotContain("AC-1", criteria, StringComparison.Ordinal);
    }

    // ---------- attachments ----------

    [Fact]
    public void An_image_is_saved_and_the_markdown_points_at_the_local_copy()
    {
        // An attachment URL needs the PAT to fetch. Left untouched it is a broken image for a person
        // and nothing at all for the model — losing evidence the ticket does carry.
        const string Url = "https://dev.azure.com/contoso/_apis/wit/attachments/abc-123";
        Write(Prose($"antes ![captura]({Url}) después"), new TicketAttachment("captura.png", [1, 2, 3], Url));

        var page = File.ReadAllText(Path.Combine(_directory, "ticket.md"));

        Assert.Contains("![captura](attachments/captura.png)", page, StringComparison.Ordinal);
        Assert.DoesNotContain(Url, page, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_directory, "attachments", "captura.png")));
    }

    [Fact]
    public void Two_attachments_sharing_a_name_both_survive()
    {
        // Azure keys attachments by GUID, so one work item can carry two files called the same
        // thing. Letting the second overwrite the first loses evidence silently.
        Write(
            Prose("dos capturas"),
            new TicketAttachment("captura.png", [1], "https://x/a"),
            new TicketAttachment("captura.png", [2], "https://x/b"));

        var saved = Directory.GetFiles(Path.Combine(_directory, "attachments")).Select(Path.GetFileName).Order().ToList();

        Assert.Equal(["captura-2.png", "captura.png"], saved);
    }

    [Fact]
    public void An_attachment_removed_from_the_ticket_stops_being_mirrored()
    {
        Write(Prose("con adjunto"), new TicketAttachment("vieja.png", [1], "https://x/a"));
        Write(Prose("sin adjunto"));

        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "attachments")));
    }

    [Fact]
    public void The_users_notes_are_read_back_for_the_review_to_see()
    {
        Write(Prose("el detalle"));
        var notes = Path.Combine(_directory, "notes");
        File.WriteAllText(Path.Combine(notes, "acuerdo.md"), "En la reunión se decidió no tocar el legacy.");
        File.WriteAllText(Path.Combine(notes, "captura.png"), "binario");

        var read = TicketMirror.ReadNotes(_directory, 10_000);

        // What the ticket leaves unsaid is exactly what a review judging "does this deliver it" is
        // missing. Text only: a screenshot in there is not something to paste into a prompt.
        Assert.Contains("acuerdo.md", read, StringComparison.Ordinal);
        Assert.Contains("no tocar el legacy", read, StringComparison.Ordinal);
        Assert.DoesNotContain("binario", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_the_notes_never_writes_to_them()
    {
        Write(Prose("el detalle"));
        File.WriteAllText(Path.Combine(_directory, "notes", "mia.md"), "mía");

        TicketMirror.ReadNotes(_directory, 10_000);
        Write(Prose("otra vez"));

        // `WI-003` still holds: the one direction data moves out of `notes/` leaves it untouched.
        Assert.Equal(["mia.md"], Directory.GetFiles(Path.Combine(_directory, "notes")).Select(Path.GetFileName));
        Assert.Equal("mía", File.ReadAllText(Path.Combine(_directory, "notes", "mia.md")));
    }

    [Fact]
    public void Notes_are_cut_at_their_budget_so_one_long_note_cannot_crowd_out_the_diff()
    {
        Write(Prose("el detalle"));
        File.WriteAllText(Path.Combine(_directory, "notes", "larga.md"), new string('x', 5_000));

        Assert.Equal(200, TicketMirror.ReadNotes(_directory, 200).Length);
    }

    [Fact]
    public void A_ticket_with_no_notes_directory_reads_as_nothing()
    {
        // The ordinary case: the directory is created empty and most tickets never get a note.
        Assert.Equal(string.Empty, TicketMirror.ReadNotes(Path.Combine(_directory, "nunca-sincronizado"), 10_000));
    }

    [Fact]
    public void An_attachment_that_could_not_be_downloaded_is_named_rather_than_omitted()
    {
        TicketMirror.Write(
            _directory, Sample(), Prose("texto"), "{}", [],
            ["`enorme.zip` — 40960 KB, no cabe en el presupuesto restante"]);

        var page = File.ReadAllText(Path.Combine(_directory, "ticket.md"));

        Assert.Contains("Adjuntos no descargados", page, StringComparison.Ordinal);
        Assert.Contains("enorme.zip", page, StringComparison.Ordinal);
    }
}
