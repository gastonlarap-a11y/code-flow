using System.Text.Json;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// The generated <c>--mcp-config</c> file.
/// See <c>docs/business-rules/09-workspace-scoped.md</c>.
/// </summary>
public sealed class McpConfigTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"codeflow-mcp-{Guid.NewGuid():N}");

    [Fact]
    public void A_workspace_with_no_enabled_server_produces_no_file_and_no_flag()
    {
        // Null is meaningful: it is what makes the invocation omit --mcp-config entirely rather than
        // pass an empty server map, which some CLIs treat as an error rather than as "no servers".
        Assert.Null(McpConfig.Write([], Destination));
        Assert.Null(McpConfig.Write([Server("Disabled", enabled: false)], Destination));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public void Only_the_enabled_servers_are_written()
    {
        var path = McpConfig.Write(
            [Server("kept"), Server("dropped", enabled: false)],
            Destination);

        Assert.Equal(Destination, path);

        var servers = Servers(path!);
        Assert.Equal(["kept"], servers.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void The_argument_line_is_split_on_whitespace_and_the_env_on_the_first_equals()
    {
        var path = McpConfig.Write(
            [Server("files", command: "npx", args: "  -y   @scope/server   --port 8080  ",
                env: "TOKEN=abc=def\n  REGION = eu-west-1  \nnot-a-pair\n")],
            Destination);

        var server = Servers(path!).GetProperty("files");

        Assert.Equal("npx", server.GetProperty("command").GetString());
        Assert.Equal(
            ["-y", "@scope/server", "--port", "8080"],
            server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));

        var env = server.GetProperty("env");
        // Split on the *first* equals, so a value containing one survives.
        Assert.Equal("abc=def", env.GetProperty("TOKEN").GetString());
        // Both halves are trimmed, as in 1.7.2.
        Assert.Equal("eu-west-1", env.GetProperty("REGION").GetString());
        // A line with no equals is skipped rather than stored as an empty value.
        Assert.Equal(["TOKEN", "REGION"], env.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void An_empty_argument_or_env_line_yields_an_empty_map_rather_than_a_missing_key()
    {
        // The CLIs expect both keys present; a missing one is not the same as an empty one.
        var path = McpConfig.Write([Server("bare", args: "", env: "")], Destination);
        var server = Servers(path!).GetProperty("bare");

        Assert.Empty(server.GetProperty("args").EnumerateArray());
        Assert.Empty(server.GetProperty("env").EnumerateObject());
    }

    [Fact]
    public void Rewriting_replaces_the_previous_file_rather_than_merging_into_it()
    {
        McpConfig.Write([Server("first")], Destination);
        McpConfig.Write([Server("second")], Destination);

        Assert.Equal(["second"], Servers(Destination).EnumerateObject().Select(p => p.Name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Destination => Path.Combine(_directory, "workspaces", "ws-1", "mcp.json");

    private static JsonElement Servers(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("mcpServers");

    private static WorkspaceMcp Server(
        string name,
        string command = "node",
        string args = "server.js",
        string env = "",
        bool enabled = true) =>
        new($"id-{name}", "ws-1", name, command, args, env, enabled, "2026-07-29T00:00:00.0000000+00:00");
}
