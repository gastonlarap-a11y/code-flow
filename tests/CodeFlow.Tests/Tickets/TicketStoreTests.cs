using CodeFlow.Storage;
using CodeFlow.Tickets;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// The ticket cache and the branch links, against a real migrated database.
/// </summary>
public sealed class TicketStoreTests : IDisposable
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

    private static Ticket Sample(string externalId = "426647", string type = "Product Backlog Item") => new(
        TicketStore.IdFor("azure", "contoso", "Web", externalId),
        "azure", "contoso", "Web", externalId,
        $"Ticket {externalId}", "Ready to Test", type, "Ada Lovelace",
        $"https://dev.azure.com/contoso/Web/_workitems/edit/{externalId}",
        23, "/tmp/mirror", Clock.Now());

    [Fact]
    public void A_ticket_round_trips_through_the_cache()
    {
        using var connection = Open();
        var ticket = Sample();

        TicketStore.Upsert(connection, ticket, """{"id":426647}""");

        Assert.Equal(ticket, TicketStore.Get(connection, ticket.Id));
        Assert.Equal("""{"id":426647}""", TicketStore.RawJson(connection, ticket.Id));
    }

    [Fact]
    public void Re_syncing_updates_the_row_instead_of_adding_one()
    {
        using var connection = Open();
        var (projectId, _) = Project(connection);

        TicketStore.Upsert(connection, Sample(), "{}");
        TicketStore.Link(connection, projectId, "feature/1234", Sample().Id);

        var moved = Sample() with { State = "Done", Rev = 24 };
        TicketStore.Upsert(connection, moved, """{"rev":24}""");

        // The link survives, which is the point of an upsert here: a delete-then-insert would
        // cascade ticket_links away on every refresh.
        Assert.Equal("Done", TicketStore.Get(connection, moved.Id)!.State);
        Assert.NotNull(TicketStore.ForBranch(connection, projectId, "feature/1234"));
    }

    [Fact]
    public void A_branch_points_at_one_ticket_and_relinking_replaces_it()
    {
        using var connection = Open();
        var (projectId, _) = Project(connection);

        TicketStore.Upsert(connection, Sample("111"), "{}");
        TicketStore.Upsert(connection, Sample("222"), "{}");

        TicketStore.Link(connection, projectId, "feature/x", Sample("111").Id);
        TicketStore.Link(connection, projectId, "feature/x", Sample("222").Id);

        Assert.Equal("222", TicketStore.ForBranch(connection, projectId, "feature/x")!.ExternalId);
    }

    [Fact]
    public void An_unlinked_branch_has_no_ticket()
    {
        using var connection = Open();
        var (projectId, _) = Project(connection);

        TicketStore.Upsert(connection, Sample(), "{}");
        TicketStore.Link(connection, projectId, "feature/x", Sample().Id);
        TicketStore.Unlink(connection, projectId, "feature/x");

        Assert.Null(TicketStore.ForBranch(connection, projectId, "feature/x"));
        // The ticket itself stays cached: unlinking a branch is not forgetting the ticket.
        Assert.NotNull(TicketStore.Get(connection, Sample().Id));
    }

    [Fact]
    public void A_repository_lists_the_tickets_it_has_linked()
    {
        using var connection = Open();
        var (projectId, _) = Project(connection);

        TicketStore.Upsert(connection, Sample("111"), "{}");
        TicketStore.Upsert(connection, Sample("222"), "{}");
        TicketStore.Link(connection, projectId, "a", Sample("111").Id);

        var listed = TicketStore.List(connection, projectId);

        // Only the linked one: a ticket cached but attached to nothing is not this repository's work.
        Assert.Equal(["111"], listed.Select(entry => entry.Ticket.ExternalId));
    }

    [Fact]
    public void Another_repositorys_tickets_are_not_this_ones()
    {
        // The scope the module answers for. It was workspace-wide first, and using it settled the
        // question: a list that mixes in another repository's tickets answers a question this view
        // never asked.
        using var connection = Open();
        var (mine, workspaceId) = Project(connection);
        var theirs = Project(connection, workspaceId).ProjectId;

        TicketStore.Upsert(connection, Sample("111"), "{}");
        TicketStore.Upsert(connection, Sample("222"), "{}");
        TicketStore.Link(connection, mine, "feature/x", Sample("111").Id);
        TicketStore.Link(connection, theirs, "feature/y", Sample("222").Id);

        Assert.Equal(["111"], TicketStore.List(connection, mine).Select(e => e.Ticket.ExternalId));
        Assert.Equal(["222"], TicketStore.List(connection, theirs).Select(e => e.Ticket.ExternalId));
    }

    [Fact]
    public void A_link_outlives_the_branch_it_names()
    {
        // Nothing deletes a link when a git branch is deleted — the only DELETE is `Unlink`, the
        // explicit button. A merged branch is deleted as a matter of course, and the record of what
        // it was work for is precisely what you want afterwards (`WI-021`). Asserted because it is
        // a property of the *absence* of code, which is the kind that gets added by accident.
        using var connection = Open();
        var (projectId, _) = Project(connection);

        TicketStore.Upsert(connection, Sample(), "{}");
        TicketStore.Link(connection, projectId, "feature/long-gone", Sample().Id);

        var link = Assert.Single(Assert.Single(TicketStore.List(connection, projectId)).Links);

        Assert.Equal("feature/long-gone", link.Branch);
    }

    [Fact]
    public void A_ticket_worked_on_from_two_branches_is_one_entry_with_two_links()
    {
        // What `SELECT DISTINCT` used to collapse, and the reason the branch never reached the
        // screen: the join already produces a row per link, and throwing the duplicates away threw
        // the branch away with them (`WI-021`).
        using var connection = Open();
        var (projectId, _) = Project(connection);

        TicketStore.Upsert(connection, Sample(), "{}");
        TicketStore.Link(connection, projectId, "feature/first", Sample().Id);
        TicketStore.Link(connection, projectId, "feature/second", Sample().Id);

        var entry = Assert.Single(TicketStore.List(connection, projectId));

        Assert.Equal("426647", entry.Ticket.ExternalId);
        Assert.Equal(["feature/first", "feature/second"], entry.Links.Select(link => link.Branch));
    }

    [Fact]
    public void Every_link_carries_the_repositorys_name_not_only_its_id()
    {
        // The name is what a row prints. An id on screen answers nothing, which is the whole point
        // of the join to `projects`.
        using var connection = Open();
        var (projectId, _) = Project(connection);

        TicketStore.Upsert(connection, Sample(), "{}");
        TicketStore.Link(connection, projectId, "feature/x", Sample().Id);

        var link = Assert.Single(Assert.Single(TicketStore.List(connection, projectId)).Links);

        Assert.Equal(projectId, link.ProjectId);
        Assert.Equal("repo", link.ProjectName);
        Assert.Equal("feature/x", link.Branch);
    }

    [Fact]
    public void Others_of_the_same_type_are_what_a_template_is_recognised_against()
    {
        using var connection = Open();

        TicketStore.Upsert(connection, Sample("111"), """{"a":1}""");
        TicketStore.Upsert(connection, Sample("222"), """{"a":2}""");
        TicketStore.Upsert(connection, Sample("333", type: "Bug"), """{"a":3}""");

        var others = TicketStore.OthersOfType(connection, "azure", "contoso", "Web", "Product Backlog Item", Sample("111").Id);

        // Same board and same type only: two backlog items share a refinement form, a Bug does not.
        Assert.Equal(["""{"a":2}"""], others);
    }

    private SqliteConnection Open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeflow-ticketstore-{Guid.NewGuid():N}.db");
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

    /// <summary>
    /// A project, in a new workspace or in one that already exists.
    /// </summary>
    /// <remarks>
    /// Both are needed because <c>ticket_links</c> references the project and the project references
    /// the workspace — and because the scope test needs two repositories that share one workspace,
    /// which is exactly the arrangement the old workspace-wide list conflated.
    /// </remarks>
    private static (string ProjectId, string WorkspaceId) Project(
        SqliteConnection connection, string? workspaceId = null)
    {
        var workspace = workspaceId ?? WorkspaceStore.Create(connection, "Flow", "folder", "#6366f1").Id;
        var projectId = Guid.NewGuid().ToString();

        Sql.Execute(connection,
            """
            INSERT INTO projects (id, workspace_id, name, local_path, created_at)
            VALUES ($id, $workspaceId, 'repo', '/tmp/repo', $createdAt)
            """,
            ("$id", projectId), ("$workspaceId", workspace), ("$createdAt", Clock.Now()));

        return (projectId, workspace);
    }
}
