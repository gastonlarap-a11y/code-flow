using System.Text.Json;
using CodeFlow.Ipc;
using CodeFlow.Platform;
using CodeFlow.Tests.Ipc;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// The workspace commands as the renderer actually reaches them: over a real socket, through the
/// real registry, asserting the raw JSON on the wire.
/// </summary>
/// <remarks>
/// <para>
/// The store tests next door call these features directly and never serialise anything, so nothing
/// in the suite would notice if the naming policy flipped to camelCase, if <c>create_project</c>
/// stopped reading its nested payload, or if "no such project" started coming back as an error
/// instead of <c>null</c>. Each of those compiles, passes those tests, and then shows the user
/// blank rows.
/// </para>
/// <para>
/// So the assertions here deliberately work on <see cref="JsonElement"/> and property names, not on
/// deserialised records — deserialising with the same context that serialised would make the test
/// agree with any rename.
/// </para>
/// </remarks>
public sealed class WorkspaceIpcTests : IAsyncLifetime
{
    private const string Token = "workspace-ipc-token";

    private TempDatabase _db = null!;
    private string _endpoint = null!;
    private IIpcListener _listener = null!;
    private IpcServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serving = null!;
    private long _nextId;

    public ValueTask InitializeAsync()
    {
        _db = new TempDatabase();
        _endpoint = Ipc.TestEndpoint.Create();

        // The real registration path, not a hand-built table: if a command is registered under the
        // wrong name, or not registered at all, that is exactly what this should catch.
        var registry = new CommandRegistry()
            .AddAppCommands()
            .AddWorkspaceCommands(_db.Handle)
            .Seal();

        _cts = new CancellationTokenSource();
        _listener = IpcListener.Create(_endpoint);
        _server = new IpcServer(registry, Token);
        _serving = _server.RunAsync(_listener, _cts.Token);

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        await _server.DisposeAsync();
        await _listener.DisposeAsync();
        _cts.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_created_workspace_crosses_the_wire_in_snake_case()
    {
        await using var client = await ConnectAsync();

        var workspace = await CallAsync(client, "create_workspace",
            """{"name":"First","icon":"folder","color":"#6366f1"}""");

        // The renderer's Workspace interface reads these exact keys. A camelCase policy would
        // serialise, deserialise and render nothing.
        Assert.Equal("First", workspace.GetProperty("name").GetString());
        Assert.Equal(0, workspace.GetProperty("sort_order").GetInt64());
        Assert.NotEmpty(workspace.GetProperty("created_at").GetString()!);
        Assert.False(workspace.TryGetProperty("sortOrder", out _));
        Assert.False(workspace.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task A_project_round_trips_through_its_nested_snake_case_input()
    {
        await using var client = await ConnectAsync();
        var workspaceId = (await CallAsync(client, "create_workspace",
            """{"name":"First","icon":"folder","color":"#6366f1"}""")).GetProperty("id").GetString();

        // Unlike every other parameter, NewProject's own keys are snake_case: it is a whole object
        // in 1.7.2, not a parameter list, so it never got the camelCase translation.
        // Three '$' because the payload's own nested object ends in "}}", which two would read as
        // an interpolation delimiter.
        var project = await CallAsync(client, "create_project",
            $$$"""
            {"input":{"workspace_id":"{{{workspaceId}}}","name":"Repo","local_path":"/tmp/repo",
              "remote_url":null,"color":"#6366f1","icon":"git-branch","ado_org":null,
              "ado_project":null,"ado_repo_id":null,"github_owner":"owner","github_repo":"repo",
              "github_host":null}}
            """);

        Assert.Equal(workspaceId, project.GetProperty("workspace_id").GetString());
        Assert.Equal("/tmp/repo", project.GetProperty("local_path").GetString());
        Assert.Equal("owner", project.GetProperty("github_owner").GetString());

        // An unset link column stays null on the wire rather than collapsing to "".
        Assert.Equal(JsonValueKind.Null, project.GetProperty("ado_org").ValueKind);

        var listed = await CallAsync(client, "list_projects", $$"""{"workspaceId":"{{workspaceId}}"}""");
        Assert.Equal(JsonValueKind.Array, listed.ValueKind);
        Assert.Equal(project.GetProperty("id").GetString(), listed[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task An_unknown_project_resolves_to_null_rather_than_an_error()
    {
        await using var client = await ConnectAsync();

        var response = await SendAsync(client, "get_project", """{"id":"nope"}""");

        // The renderer types this as `Project | null` and branches on it. An error here would
        // reject the promise and surface as a toast instead of an empty state.
        Assert.False(response.TryGetProperty("error", out _));
        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task A_setting_stored_blank_comes_back_as_an_empty_string_not_null()
    {
        await using var client = await ConnectAsync();

        Assert.Equal(JsonValueKind.Null,
            (await SendAsync(client, "get_setting", """{"key":"ai_provider"}""")).GetProperty("result").ValueKind);

        await CallAsync(client, "set_setting", """{"key":"ai_provider","value":""}""");

        // WS-004 on the wire: a stored empty value is a real row, and the two states have to stay
        // distinguishable from the renderer's side.
        var stored = await CallAsync(client, "get_setting", """{"key":"ai_provider"}""");
        Assert.Equal(JsonValueKind.String, stored.ValueKind);
        Assert.Equal(string.Empty, stored.GetString());
    }

    [Fact]
    public async Task Saving_a_blank_prompt_restores_the_built_in()
    {
        await using var client = await ConnectAsync();
        var workspaceId = (await CallAsync(client, "create_workspace",
            """{"name":"First","icon":"folder","color":"#6366f1"}""")).GetProperty("id").GetString();

        var builtin = (await CallAsync(client, "default_workspace_prompt",
            """{"kind":"review_standard"}""")).GetString();

        await CallAsync(client, "set_workspace_prompt",
            $$"""{"workspaceId":"{{workspaceId}}","kind":"review_standard","content":"mine"}""");
        Assert.Equal("mine", (await CallAsync(client, "get_workspace_prompt",
            $$"""{"workspaceId":"{{workspaceId}}","kind":"review_standard"}""")).GetString());

        await CallAsync(client, "set_workspace_prompt",
            $$"""{"workspaceId":"{{workspaceId}}","kind":"review_standard","content":""}""");

        // STORE-012 end to end: the blank save is the reset, and the reset resolves to the builtin.
        Assert.Equal(builtin, (await CallAsync(client, "get_workspace_prompt",
            $$"""{"workspaceId":"{{workspaceId}}","kind":"review_standard"}""")).GetString());
    }

    [Fact]
    public async Task An_upsert_with_a_null_id_mints_one()
    {
        await using var client = await ConnectAsync();
        var workspaceId = (await CallAsync(client, "create_workspace",
            """{"name":"First","icon":"folder","color":"#6366f1"}""")).GetProperty("id").GetString();

        // The renderer sends an explicit null for "this is new", which has to reach the handler as
        // an absent id rather than throwing on a missing parameter.
        var created = await CallAsync(client, "upsert_review_context",
            $$"""{"id":null,"workspaceId":"{{workspaceId}}","name":"Context","content":"body","enabled":true}""");

        Assert.NotEmpty(created.GetProperty("id").GetString()!);
        Assert.True(created.GetProperty("enabled").GetBoolean());

        var listed = await CallAsync(client, "list_review_contexts", $$"""{"workspaceId":"{{workspaceId}}"}""");
        Assert.Equal(1, listed.GetArrayLength());
    }

    [Fact]
    public async Task A_failing_command_reaches_the_client_as_an_error_string()
    {
        await using var client = await ConnectAsync();
        var workspaceId = (await CallAsync(client, "create_workspace",
            """{"name":"First","icon":"folder","color":"#6366f1"}""")).GetProperty("id").GetString();
        var projectId = (await CallAsync(client, "create_project",
            $$$"""
            {"input":{"workspace_id":"{{{workspaceId}}}","name":"Repo","local_path":"/tmp/repo",
              "remote_url":null,"color":"#6366f1","icon":"git-branch"}}
            """)).GetProperty("id").GetString();

        var response = await SendAsync(client, "move_project_to_workspace",
            $$"""{"id":"{{projectId}}","workspaceId":"does-not-exist"}""");

        // The foreign key rejects it (AMBIGUOUS-WS-a). What matters here is that the rejection
        // arrives as a reply the renderer can show, not as a dropped frame or a dead connection.
        Assert.Contains("FOREIGN KEY", response.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // `reset_app_data` is deliberately not exercised here. It writes its marker to the real
    // AppPaths.ResetMarkerFile — this machine's own ~/CodeFlow — and a marker left behind by a
    // crashed test would make the next real launch wipe the user's database. AppPaths has no
    // injection point for the base directory, so the only safe test would be one that first
    // introduces one; until then, registration is asserted in WorkspaceCommandsTests and the
    // marker write is covered by the manual end-to-end pass.

    // -----------------------------------------------------------------------

    private Task<IpcTestClient> ConnectAsync() => IpcTestClient.ConnectAsync(_endpoint, "rpc", Token);

    /// <summary>Invokes a command and returns its <c>result</c>, failing the test on an error reply.</summary>
    private async Task<JsonElement> CallAsync(IpcTestClient client, string method, string parameters)
    {
        var response = await SendAsync(client, method, parameters);

        Assert.False(response.TryGetProperty("error", out var error),
            $"{method} failed: {(error.ValueKind == JsonValueKind.Undefined ? "" : error.GetString())}");

        return response.GetProperty("result");
    }

    private async Task<JsonElement> SendAsync(IpcTestClient client, string method, string parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        await client.SendAsync($$"""{"id":{{id}},"method":"{{method}}","params":{{parameters}}}""");

        var response = await client.ReceiveAsync();
        Assert.Equal(id, response.GetProperty("id").GetInt64());
        return response;
    }
}
