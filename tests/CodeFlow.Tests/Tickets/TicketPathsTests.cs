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

        Assert.Equal(Path.Combine("/root", "achsdev", "portal-web", "1234-login-con-sso"), directory);
    }

    [Fact]
    public void A_ticket_with_no_usable_title_still_gets_a_directory()
    {
        // A title of only punctuation slugs to nothing; the id alone still identifies the ticket.
        var directory = TicketPaths.DirectoryFor("/root", "org", "proj", "77", "***");

        Assert.Equal(Path.Combine("/root", "org", "proj", "77"), directory);
    }

    [Theory]
    // Accents fold to their base letter rather than to a separator, so the name stays readable.
    // These pin the explicit fold table: the project builds with InvariantGlobalization, so
    // Normalize(FormD) is a no-op and would leave "facturaci-n" behind.
    [InlineData("Facturación", "facturacion")]
    [InlineData("Añadir año", "anadir-ano")]
    [InlineData("ÑOÑO", "nono")]
    [InlineData("Straße", "strasse")]
    // A script with no ASCII spelling still degrades to a separator rather than being invented.
    [InlineData("请求", "")]
    // Anything outside ASCII letters and digits becomes a single hyphen — including the separators
    // that would otherwise create a directory level nobody asked for.
    [InlineData("Payments/Core", "payments-core")]
    [InlineData("fix: the thing", "fix-the-thing")]
    [InlineData("a  --  b", "a-b")]
    [InlineData("  trimmed  ", "trimmed")]
    [InlineData("ALL CAPS", "all-caps")]
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
