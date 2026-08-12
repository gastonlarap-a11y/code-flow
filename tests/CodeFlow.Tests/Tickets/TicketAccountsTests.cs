using CodeFlow.Storage;
using CodeFlow.Tickets;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// Which Azure DevOps account a project's tickets come from.
/// </summary>
/// <remarks>
/// The case that matters most is the last one: with nothing to decide it, this must say so rather
/// than pick a connection. An app that quietly reads the wrong organisation's board shows an empty
/// list and blames the board.
/// </remarks>
public sealed class TicketAccountsTests : IDisposable
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
    public void The_workspaces_own_choice_wins_over_the_repositorys_link()
    {
        // The whole reason the column exists: the board can live in a different organisation from
        // the code, which is what having work and personal projects in one app looks like.
        using var connection = Open();
        var (projectId, workspaceId) = Project(connection, projectOrg: "acme", boardProject: "Web");

        WorkspaceStore.UpdateTicketAccount(connection, workspaceId, "achsdev", project: null);

        var account = TicketAccounts.Resolve(connection, projectId);

        Assert.Equal("achsdev", account.Org);
        Assert.Equal("Web", account.Project);
        Assert.Equal(TicketAccounts.FromWorkspace, account.Source);
    }

    [Fact]
    public void The_board_project_can_be_chosen_where_the_repository_names_none()
    {
        // The case that made this necessary: a repository hosted on GitHub has no
        // `projects.ado_project` at all, so before this the organisation resolved, the module
        // rendered, and the picker then failed with "choose an account in settings" — the very
        // thing the user had just done.
        using var connection = Open();
        var (projectId, workspaceId) = Project(connection, projectOrg: null, boardProject: null);

        WorkspaceStore.UpdateTicketAccount(connection, workspaceId, "kakaroto044", "app-personales");

        var account = TicketAccounts.Resolve(connection, projectId);

        Assert.Equal("kakaroto044", account.Org);
        Assert.Equal("app-personales", account.Project);
        Assert.Equal(TicketAccounts.FromWorkspace, account.Source);
    }

    [Fact]
    public void The_workspaces_board_project_wins_over_the_repositorys()
    {
        using var connection = Open();
        var (projectId, workspaceId) = Project(connection, projectOrg: "acme", boardProject: "Web");

        WorkspaceStore.UpdateTicketAccount(connection, workspaceId, "achsdev", "Tablero");

        Assert.Equal("Tablero", TicketAccounts.Resolve(connection, projectId).Project);
    }

    [Fact]
    public void Clearing_the_board_project_falls_back_to_the_repositorys()
    {
        using var connection = Open();
        var (projectId, workspaceId) = Project(connection, projectOrg: "acme", boardProject: "Web");

        WorkspaceStore.UpdateTicketAccount(connection, workspaceId, "achsdev", "Tablero");
        WorkspaceStore.UpdateTicketAccount(connection, workspaceId, "achsdev", project: null);

        Assert.Equal("Web", TicketAccounts.Resolve(connection, projectId).Project);
    }

    [Fact]
    public void Without_a_choice_the_repositorys_own_organisation_is_used()
    {
        using var connection = Open();
        var (projectId, _) = Project(connection, projectOrg: "acme", boardProject: "Web");

        var account = TicketAccounts.Resolve(connection, projectId);

        Assert.Equal("acme", account.Org);
        Assert.Equal(TicketAccounts.FromProject, account.Source);
    }

    [Fact]
    public void With_exactly_one_connection_that_one_is_the_only_thing_it_could_mean()
    {
        using var connection = Open();
        var (projectId, _) = Project(connection, projectOrg: null, boardProject: null);
        Settings.SetSetting(connection, "ado_connections", """[{"org":"achsdev"}]""");

        var account = TicketAccounts.Resolve(connection, projectId);

        Assert.Equal("achsdev", account.Org);
        Assert.Equal(TicketAccounts.FromOnlyConnection, account.Source);
    }

    [Fact]
    public void With_two_connections_and_nothing_chosen_it_refuses_to_guess()
    {
        using var connection = Open();
        var (projectId, _) = Project(connection, projectOrg: null, boardProject: null);
        Settings.SetSetting(connection, "ado_connections", """[{"org":"achsdev"},{"org":"kakaroto004"}]""");

        var account = TicketAccounts.Resolve(connection, projectId);

        Assert.Null(account.Org);
        Assert.Equal(TicketAccounts.Undecided, account.Source);
    }

    [Fact]
    public void With_no_connections_at_all_it_also_refuses()
    {
        using var connection = Open();
        var (projectId, _) = Project(connection, projectOrg: null, boardProject: null);

        Assert.Equal(TicketAccounts.Undecided, TicketAccounts.Resolve(connection, projectId).Source);
    }

    [Fact]
    public void Clearing_the_workspace_choice_falls_back_rather_than_to_nothing()
    {
        using var connection = Open();
        var (projectId, workspaceId) = Project(connection, projectOrg: "acme", boardProject: "Web");

        WorkspaceStore.UpdateTicketAccount(connection, workspaceId, "achsdev", project: null);
        WorkspaceStore.UpdateTicketAccount(connection, workspaceId, null, null);

        Assert.Equal(TicketAccounts.FromProject, TicketAccounts.Resolve(connection, projectId).Source);
    }

    [Fact]
    public void A_blank_choice_counts_as_no_choice()
    {
        // The settings screen clears a field by writing "", not by deleting the row.
        using var connection = Open();
        var (projectId, workspaceId) = Project(connection, projectOrg: "acme", boardProject: "Web");

        WorkspaceStore.UpdateTicketAccount(connection, workspaceId, "   ", "   ");

        Assert.Equal(TicketAccounts.FromProject, TicketAccounts.Resolve(connection, projectId).Source);
    }

    [Fact]
    public void A_malformed_connections_setting_does_not_break_the_module()
    {
        // The setting is free-form JSON the renderer owns. A shape this does not recognise must not
        // make tickets unusable — the settings screen is where it gets fixed.
        using var connection = Open();
        var (projectId, _) = Project(connection, projectOrg: null, boardProject: null);
        Settings.SetSetting(connection, "ado_connections", "no soy json");

        Assert.Equal(TicketAccounts.Undecided, TicketAccounts.Resolve(connection, projectId).Source);
    }

    [Fact]
    public void An_unknown_project_is_a_caller_error_not_an_undecided_account()
    {
        using var connection = Open();

        Assert.Throws<ArgumentException>(() => TicketAccounts.Resolve(connection, "no-such-project"));
    }

    private SqliteConnection Open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeflow-ticketacct-{Guid.NewGuid():N}.db");
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

    private static (string ProjectId, string WorkspaceId) Project(
        SqliteConnection connection, string? projectOrg, string? boardProject)
    {
        var workspace = WorkspaceStore.Create(connection, "Flow", "folder", "#6366f1");
        var projectId = Guid.NewGuid().ToString();

        Sql.Execute(connection,
            """
            INSERT INTO projects (id, workspace_id, name, local_path, ado_org, ado_project, created_at)
            VALUES ($id, $workspaceId, 'repo', '/tmp/repo', $adoOrg, $adoProject, $createdAt)
            """,
            ("$id", projectId),
            ("$workspaceId", workspace.Id),
            ("$adoOrg", projectOrg),
            ("$adoProject", boardProject),
            ("$createdAt", Clock.Now()));

        return (projectId, workspace.Id);
    }
}
