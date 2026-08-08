using System.Text.Json;
using CodeFlow.ApiClient;
using CodeFlow.Ipc;
using CodeFlow.Storage;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.ApiClient;

/// <summary>
/// The four commands the renderer fires the moment a workspace exists.
/// </summary>
/// <remarks>
/// <para>
/// This is a regression test for a defect the packaged app actually showed: an error toast reading
/// <c>unknown command 'api_load_tree'</c> on every launch. <c>Sidebar</c> mounts unconditionally and
/// auto-selects a workspace, which fires a top-level effect in <c>App.tsx</c> that hydrates the API
/// store — <c>apiStore.init()</c> issues these four in one <c>Promise.all</c>, long before the API
/// tab is ever opened, and that tab is lazy so it usually never is.
/// </para>
/// <para>
/// Only <c>api_load_tree</c> was named in the toast because <c>Promise.all</c> rejects with the
/// first rejection; all four were failing. So all four are asserted here, over a freshly migrated
/// database with nothing in it — which is exactly the state a new install starts from.
/// </para>
/// </remarks>
public sealed class ApiStartupTests
{
    /// <summary>Every command <c>apiStore.init()</c> issues, in the order it lists them.</summary>
    private static readonly string[] OnStartup =
        ["api_load_tree", "api_list_environments", "api_list_history", "api_list_cookies"];

    [Fact]
    public async Task The_four_commands_the_app_issues_on_launch_all_answer()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;

        var registry = new CommandRegistry().AddApiCommands(database.Handle);

        foreach (var command in OnStartup)
        {
            Assert.True(registry.TryGet(command, out var handler), $"{command} is not registered");

            using var arguments = JsonDocument.Parse(
                JsonSerializer.Serialize(new { workspaceId = workspace, limit = 100 }));

            var reply = await handler(arguments.RootElement, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, reply.Length);
        }
    }

    /// <summary>
    /// A brand-new workspace answers with an empty tree rather than failing.
    /// </summary>
    /// <remarks>
    /// The store hydrates before the user has created anything, so "nothing yet" has to be a
    /// successful, empty answer — not an error the UI would have to distinguish from a real one.
    /// </remarks>
    [Fact]
    public void A_workspace_with_nothing_in_it_loads_an_empty_tree()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;

        var tree = database.Use(c => ApiTreeStore.LoadTree(c, workspace));

        Assert.Empty(tree.Collections);
        Assert.Empty(tree.Folders);
        Assert.Empty(tree.Requests);
        Assert.Empty(database.Use(c => ApiHistoryStore.List(c, workspace, 100)));
        Assert.Empty(database.Use(c => ApiCookieStore.List(c, workspace)));
    }

    /// <summary>
    /// A workspace gets its Globals environment at the <em>next</em> launch, not when it is created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>STORE-008</c> is explicit that <c>ensure_globals_environment</c> "runs every launch",
    /// sweeping every workspace that has no row with <c>is_global = 1</c>. Nothing seeds one at
    /// creation time — <c>create_workspace</c> seeds the two prompt overrides and stops there.
    /// </para>
    /// <para>
    /// The consequence is real and worth stating: a workspace created during a session has no
    /// Globals environment until the app is restarted, so its variables tab is empty where an older
    /// workspace's is not. CodeFlow 1.7.2 behaves the same way and no rule calls it a defect, so it
    /// is reproduced — but it is also why <c>api_list_environments</c> has to tolerate returning
    /// nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_workspace_gets_its_globals_environment_on_the_next_launch_not_on_creation()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;

        Assert.Empty(database.Use(c => ApiEnvironmentStore.List(c, workspace)));

        // What a restart does: the migration runner sweeps every workspace lacking one.
        database.Do(Migrations.Run);

        var globals = Assert.Single(database.Use(c => ApiEnvironmentStore.List(c, workspace)));
        Assert.True(globals.IsGlobal);
        Assert.Equal("Globals", globals.Name);

        // Seeded with sort_order -1, which is what keeps it first however many are added later.
        Assert.Equal(-1, globals.SortOrder);
    }

    /// <summary>Sweeping twice does not seed a second Globals row.</summary>
    /// <remarks>
    /// The guard is on <c>is_global = 1</c> rather than a fixed id, so renaming "Globals" does not
    /// cause a duplicate on the next launch either.
    /// </remarks>
    [Fact]
    public void A_second_launch_does_not_seed_a_second_globals()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;

        database.Do(Migrations.Run);
        var globals = Assert.Single(database.Use(c => ApiEnvironmentStore.List(c, workspace)));

        database.Do(c => ApiEnvironmentStore.Update(c, globals with { Name = "Renamed" }));
        database.Do(Migrations.Run);

        Assert.Equal("Renamed", Assert.Single(database.Use(c => ApiEnvironmentStore.List(c, workspace))).Name);
    }
}
