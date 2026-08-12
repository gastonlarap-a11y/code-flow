using CodeFlow.Storage;
using CodeFlow.Tickets;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// The stored ticket reviews, against a real migrated database. <c>WI-013</c>.
/// </summary>
public sealed class TicketReviewStoreTests : IDisposable
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

    private static readonly TicketCriterionVerdict[] Criteria =
    [
        new("AC-1", "El listado pagina de 20 en 20", "cumple", "src/a.ts:1-8 — el tamaño sale del ajuste", 85),
        new("AC-2", "El rendimiento no baja de 200 ms", "no verificable", "sin evidencia en el diff", 70),
    ];

    private static readonly TicketCoverage Coverage = new(
        "incompleta", "la medición de rendimiento", "nada", "la paginación está hecha",
        Relevant: true, Relevance: "corresponde — el diff toca el listado que el ticket describe");

    [Fact]
    public void A_review_round_trips_with_its_criteria_and_its_coverage()
    {
        using var connection = Open();
        var (projectId, workspaceId, ticketId) = Seed(connection);

        TicketReviewStore.Add(
            connection,
            Review(projectId, ticketId),
            workspaceId,
            new TicketReviewMeta(Coverage, "claude", "opus", "Branch"),
            findingsJson: "[]",
            diff: "diff --git a/a b/a");

        var stored = Assert.Single(TicketReviewStore.ForBranch(connection, projectId, "feature/x"));

        Assert.Equal(Criteria, stored.Criteria);
        Assert.Equal(Coverage, stored.Coverage);
        Assert.Equal("no verificable", stored.Criteria[1].Verdict);
    }

    [Fact]
    public void The_coverage_word_is_indexed_on_its_own_column()
    {
        using var connection = Open();
        var (projectId, workspaceId, ticketId) = Seed(connection);

        TicketReviewStore.Add(
            connection, Review(projectId, ticketId), workspaceId,
            new TicketReviewMeta(Coverage, "claude", "opus", "Branch"), "[]", "");

        // The word is what a history list filters on; the sentence explaining it rides in `meta`.
        Assert.Equal(
            "incompleta",
            Sql.QueryText(connection, "SELECT coverage_verdict FROM ticket_review_runs"));
    }

    [Fact]
    public void A_review_the_model_left_without_a_coverage_block_is_still_stored()
    {
        using var connection = Open();
        var (projectId, workspaceId, ticketId) = Seed(connection);

        TicketReviewStore.Add(
            connection,
            Review(projectId, ticketId) with { Coverage = null, Criteria = [] },
            workspaceId,
            new TicketReviewMeta(null, "claude", "opus", "Branch"),
            "[]",
            "");

        var stored = Assert.Single(TicketReviewStore.ForBranch(connection, projectId, "feature/x"));

        // Null is a real outcome — the model answered without the block — and losing the markdown
        // over it would throw away the half of the answer that did arrive.
        Assert.Null(stored.Coverage);
        Assert.Empty(stored.Criteria);
        Assert.Contains("CALIDAD", stored.ReviewMd, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_whose_payload_will_not_parse_still_renders_its_markdown()
    {
        using var connection = Open();
        var (projectId, workspaceId, ticketId) = Seed(connection);

        TicketReviewStore.Add(
            connection, Review(projectId, ticketId), workspaceId,
            new TicketReviewMeta(Coverage, "claude", "opus", "Branch"), "[]", "");

        Sql.Execute(connection, "UPDATE ticket_review_runs SET criteria = 'not json', meta = '{'");

        var stored = Assert.Single(TicketReviewStore.ForBranch(connection, projectId, "feature/x"));

        // One corrupt row must not take the whole history list down with it.
        Assert.Empty(stored.Criteria);
        Assert.Null(stored.Coverage);
        Assert.Contains("CALIDAD", stored.ReviewMd, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_this_branchs_reviews_come_back()
    {
        using var connection = Open();
        var (projectId, workspaceId, ticketId) = Seed(connection);
        var meta = new TicketReviewMeta(Coverage, "claude", "opus", "Branch");

        TicketReviewStore.Add(connection, Review(projectId, ticketId), workspaceId, meta, "[]", "");
        TicketReviewStore.Add(
            connection, Review(projectId, ticketId) with { Id = "run-2", Branch = "feature/y" },
            workspaceId, meta, "[]", "");

        Assert.Single(TicketReviewStore.ForBranch(connection, projectId, "feature/x"));
        Assert.Single(TicketReviewStore.ForBranch(connection, projectId, "feature/y"));
    }

    private static TicketReviewResult Review(string projectId, string ticketId) => new(
        "run-1",
        projectId,
        ticketId,
        "feature/x",
        "main",
        "abc123",
        "completo",
        "📈 CALIDAD: Fiabilidad=A Seguridad=A Mantenibilidad=A",
        Criteria,
        Coverage,
        Clock.Now());

    private SqliteConnection Open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeflow-ticketreview-{Guid.NewGuid():N}.db");
        _files.Add(path);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        connection.Open();
        Sql.Execute(connection, "PRAGMA foreign_keys = ON");
        Migrations.Run(connection);
        return connection;
    }

    /// <summary>A workspace, a project and a cached ticket — the row references all three.</summary>
    private static (string ProjectId, string WorkspaceId, string TicketId) Seed(SqliteConnection connection)
    {
        var workspace = WorkspaceStore.Create(connection, "Flow", "folder", "#6366f1");
        var projectId = Guid.NewGuid().ToString();

        Sql.Execute(connection,
            """
            INSERT INTO projects (id, workspace_id, name, local_path, created_at)
            VALUES ($id, $workspaceId, 'repo', '/tmp/repo', $createdAt)
            """,
            ("$id", projectId), ("$workspaceId", workspace.Id), ("$createdAt", Clock.Now()));

        var ticket = new Ticket(
            TicketStore.IdFor("azure", "contoso", "Web", "426647"),
            "azure", "contoso", "Web", "426647", "Paginar el listado", "Active", "Product Backlog Item",
            null, "https://dev.azure.com/contoso/Web/_workitems/edit/426647", 1, "/tmp/mirror", Clock.Now());

        TicketStore.Upsert(connection, ticket, "{}");
        return (projectId, workspace.Id, ticket.Id);
    }
}
