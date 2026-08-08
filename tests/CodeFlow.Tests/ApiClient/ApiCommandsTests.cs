using System.Text.Json;
using CodeFlow.ApiClient;
using CodeFlow.Ipc;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.ApiClient;

/// <summary>
/// The twenty-seven storage commands of the implementation, as the transport reaches them.
/// See <c>docs/business-rules/01-ipc-surface.md</c>.
/// </summary>
public sealed class ApiCommandsTests
{
    /// <summary>The exact set this group registers.</summary>
    private static readonly string[] Expected =
    [
        "api_load_tree", "api_create_collection", "api_update_collection", "api_delete_collection",
        "api_duplicate_collection", "api_reorder_collections",
        "api_create_folder", "api_update_folder", "api_delete_folder",
        "api_create_request", "api_update_request", "api_delete_request", "api_duplicate_request",
        "api_move_node",
        "api_list_environments", "api_create_environment", "api_update_environment",
        "api_delete_environment", "api_duplicate_environment",
        "api_list_history", "api_add_history", "api_delete_history", "api_clear_history",
        "api_list_cookies", "api_upsert_cookie", "api_delete_cookie", "api_clear_cookies",
    ];

    [Fact]
    public void The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        using var database = new TempDatabase();
        var registry = new CommandRegistry().AddApiCommands(database.Handle);

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("api_load_tree", "workspaceId")]
    [InlineData("api_create_collection", "workspaceId")]
    [InlineData("api_delete_collection", "id")]
    [InlineData("api_create_folder", "collectionId")]
    [InlineData("api_create_request", "collectionId")]
    [InlineData("api_move_node", "kind")]
    [InlineData("api_list_environments", "workspaceId")]
    [InlineData("api_list_history", "workspaceId")]
    [InlineData("api_list_cookies", "workspaceId")]
    [InlineData("api_clear_cookies", "workspaceId")]
    public async Task A_command_missing_its_argument_names_the_one_it_wanted(string command, string missing)
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(command, new { }).AsTask());

        Assert.Equal($"missing required parameter '{missing}'", failure.Message);
    }

    [Theory]
    [InlineData("api_update_collection", "collection")]
    [InlineData("api_update_folder", "folder")]
    [InlineData("api_update_request", "request")]
    [InlineData("api_update_environment", "environment")]
    [InlineData("api_add_history", "entry")]
    [InlineData("api_upsert_cookie", "cookie")]
    public async Task A_command_that_wants_a_whole_row_says_which_one(string command, string missing)
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(command, new { }).AsTask());

        Assert.Equal($"missing required parameter '{missing}'", failure.Message);
    }

    /// <summary>
    /// The tree's wire shape: snake_case, in both directions.
    /// </summary>
    /// <remarks>
    /// These rows travel out, get edited in the UI, and come straight back to the update commands
    /// unchanged, so one naming policy has to describe both journeys. A camelCase slip here would
    /// render an empty sidebar rather than raise an error.
    /// </remarks>
    [Fact]
    public async Task A_tree_crosses_the_wire_under_the_field_names_the_renderer_reads()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;
        var collection = database.Use(c => ApiTreeStore.CreateCollection(c, workspace, "Requests"));
        database.Use(c => ApiTreeStore.CreateRequest(c, collection.Id, null, "Login", "http", "{}"));

        var reply = await InvokeAsync("api_load_tree", new { workspaceId = workspace }, database);

        using var parsed = JsonDocument.Parse(reply);
        Assert.Equal(["collections", "folders", "requests"], parsed.RootElement.EnumerateObject().Select(p => p.Name));

        Assert.Equal(
            ["id", "workspace_id", "name", "description", "auth", "pre_script", "post_script",
             "variables", "sort_order", "created_at", "updated_at"],
            parsed.RootElement.GetProperty("collections")[0].EnumerateObject().Select(p => p.Name));

        Assert.Equal(
            ["id", "collection_id", "folder_id", "name", "protocol", "method", "url", "spec",
             "sort_order", "created_at", "updated_at"],
            parsed.RootElement.GetProperty("requests")[0].EnumerateObject().Select(p => p.Name));
    }

    /// <summary>
    /// A row the renderer sends back is read under the same snake_case names it received.
    /// </summary>
    /// <remarks>
    /// Unlike the scalar arguments beside them, which are camelCase as the renderer sends them.
    /// The two conventions meet in the same command, which is exactly why this is asserted rather
    /// than assumed.
    /// </remarks>
    [Fact]
    public async Task A_row_sent_back_for_an_update_is_read_under_its_own_field_names()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;
        var collection = database.Use(c => ApiTreeStore.CreateCollection(c, workspace, "Requests"));

        await InvokeAsync("api_update_collection", new
        {
            collection = new
            {
                id = collection.Id,
                workspace_id = workspace,
                name = "Renamed",
                description = "notes",
                auth = "{}",
                pre_script = "",
                post_script = "",
                variables = "[]",
                sort_order = 0,
                created_at = collection.CreatedAt,
                updated_at = collection.UpdatedAt,
            },
        }, database);

        var stored = Assert.Single(database.Use(c => ApiTreeStore.LoadTree(c, workspace)).Collections);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal("notes", stored.Description);
    }

    [Fact]
    public async Task A_null_folder_means_directly_under_the_collection()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;
        var collection = database.Use(c => ApiTreeStore.CreateCollection(c, workspace, "Requests"));

        var reply = await InvokeAsync("api_create_request", new
        {
            collectionId = collection.Id,
            folderId = (string?)null,
            name = "Login",
            protocol = "http",
            spec = "{}",
        }, database);

        using var parsed = JsonDocument.Parse(reply);
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("folder_id").ValueKind);
    }

    [Fact]
    public async Task A_command_that_answers_nothing_answers_null()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;

        Assert.Equal("null", await InvokeAsync("api_clear_history", new { workspaceId = workspace }, database));
        Assert.Equal("null", await InvokeAsync("api_clear_cookies", new { workspaceId = workspace }, database));
    }

    /// <summary>Dispatches a command the way the transport does, and answers its JSON reply.</summary>
    private static async ValueTask<string> InvokeAsync(
        string command, object parameters, TempDatabase? database = null)
    {
        using var owned = database is null ? new TempDatabase() : null;
        var registry = new CommandRegistry().AddApiCommands((database ?? owned!).Handle);

        Assert.True(registry.TryGet(command, out var handler));

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var reply = await handler(arguments.RootElement, TestContext.Current.CancellationToken);

        return System.Text.Encoding.UTF8.GetString(reply.Span);
    }
}
