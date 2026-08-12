using CodeFlow.Platform;
using CodeFlow.Storage;
using CodeFlow.Tickets;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// Where a ticket's mirrored files go: the redirectable root, and the slugging underneath it.
/// </summary>
public sealed class TicketPathsTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _files)
        {
            foreach (var path in new[] { file, $"{file}-wal", $"{file}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public void With_no_setting_the_root_is_the_one_under_the_app_directory()
    {
        using var connection = Open();

        Assert.Equal(AppPaths.TicketsRoot, TicketPaths.RootFor(connection));
    }

    [Fact]
    public void A_configured_root_wins()
    {
        using var connection = Open();
        Settings.SetSetting(connection, TicketPaths.RootSetting, "/somewhere/else");

        Assert.Equal("/somewhere/else", TicketPaths.RootFor(connection));
    }

    [Fact]
    public void Clearing_the_field_means_the_default_rather_than_an_empty_path()
    {
        // The settings screen clears a field by writing "", not by deleting the row. Treating that
        // as a real value would resolve every ticket directory to a relative path.
        using var connection = Open();
        Settings.SetSetting(connection, TicketPaths.RootSetting, "   ");

        Assert.Equal(AppPaths.TicketsRoot, TicketPaths.RootFor(connection));
    }

    [Fact]
    public void A_ticket_directory_is_org_then_project_then_the_id_and_title()
    {
        var directory = TicketPaths.DirectoryFor("/root", "achsdev", "Portal Web", "1234", "Login con SSO");

        Assert.Equal(Path.Combine("/root", "achsdev", "Portal-Web", "1234-Login-con-SSO"), directory);
    }

    [Fact]
    public void The_directory_reads_like_the_title_it_came_from()
    {
        // The exact story a user opened the folder for, and reported as wrong because
        // `3-cf-e2e-ajuste-de-tabla-criterios-en-prosa` was unrecognisable. What made it awkward on
        // a path was never the capitals — it was the spaces and the parentheses, and those are still
        // gone.
        var directory = TicketPaths.DirectoryFor(
            "/root", "kakaroto044", "app-personales", "3", "CF-E2E Ajuste de tabla (criterios en prosa)");

        Assert.Equal(
            Path.Combine("/root", "kakaroto044", "app-personales", "3-CF-E2E-Ajuste-de-tabla-criterios-en-prosa"),
            directory);
    }

    [Fact]
    public void A_ticket_with_no_usable_title_still_gets_a_directory()
    {
        // A title of only punctuation slugs to nothing; the id alone still identifies the ticket.
        var directory = TicketPaths.DirectoryFor("/root", "org", "proj", "77", "***");

        Assert.Equal(Path.Combine("/root", "org", "proj", "77"), directory);
    }

    [Fact]
    public void A_ticket_seen_for_the_first_time_gets_a_directory_from_its_title()
    {
        var directory = TicketPaths.MirrorFor(null, "/root", "org", "proj", "3", "Ajuste de tabla");

        Assert.Equal(Path.Combine("/root", "org", "proj", "3-Ajuste-de-tabla"), directory);
    }

    [Fact]
    public void A_renamed_ticket_keeps_the_directory_it_already_has()
    {
        // The defect this closes: the directory was recomputed from the current title on every
        // sync, so renaming the work item on the board relocated the mirror — and left the user's
        // `notes/` in a directory the app would never open again. Nothing moved them, and nothing
        // said so.
        var existing = Path.Combine("/root", "org", "proj", "3-Ajuste-de-tabla");

        var directory = TicketPaths.MirrorFor(existing, "/root", "org", "proj", "3", "Otro título del todo");

        Assert.Equal(existing, directory);
    }

    [Fact]
    public void A_blank_stored_path_counts_as_never_mirrored()
    {
        // A row written before the column meant anything: an empty string is not a location.
        var directory = TicketPaths.MirrorFor("  ", "/root", "org", "proj", "3", "Ajuste de tabla");

        Assert.Equal(Path.Combine("/root", "org", "proj", "3-Ajuste-de-tabla"), directory);
    }

    [Theory]
    // Accents fold to their base letter rather than to a separator, so the name stays readable.
    // These pin the explicit fold table: the project builds with InvariantGlobalization, so
    // Normalize(FormD) is a no-op and would leave "facturaci-n" behind.
    [InlineData("Facturación", "Facturacion")]
    [InlineData("Añadir año", "Anadir-ano")]
    [InlineData("ÑOÑO", "NONO")]
    [InlineData("Straße", "Strasse")]
    // The fold keeps the case it found, so an accented capital does not become a lower-case letter
    // in the middle of a word.
    [InlineData("ÁRBOL", "ARBOL")]
    [InlineData("árbol", "arbol")]
    // A script with no ASCII spelling still degrades to a separator rather than being invented.
    [InlineData("请求", "")]
    // Anything outside ASCII letters and digits becomes a single hyphen — including the separators
    // that would otherwise create a directory level nobody asked for.
    [InlineData("Payments/Core", "Payments-Core")]
    [InlineData("fix: the thing", "fix-the-thing")]
    [InlineData("a  --  b", "a-b")]
    [InlineData("  trimmed  ", "trimmed")]
    [InlineData("ALL CAPS", "ALL-CAPS")]
    [InlineData("***", "")]
    public void Slugging_reduces_text_to_one_safe_segment(string input, string expected)
    {
        Assert.Equal(expected, TicketPaths.Slug(input));
    }

    [Fact]
    public void A_very_long_title_is_cut_without_a_trailing_separator()
    {
        var slug = TicketPaths.Slug(new string('a', 40) + " " + new string('b', 40));

        Assert.Equal(60, slug.Length);
        Assert.DoesNotContain("--", slug, StringComparison.Ordinal);
        Assert.False(slug.EndsWith('-'));
    }

    private SqliteConnection Open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeflow-ticketpaths-{Guid.NewGuid():N}.db");
        _files.Add(path);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        connection.Open();
        Migrations.Run(connection);
        return connection;
    }
}
