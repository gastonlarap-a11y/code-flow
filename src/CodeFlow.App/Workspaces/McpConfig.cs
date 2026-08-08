using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Platform;

namespace CodeFlow.Workspaces;

/// <summary>
/// Writes a workspace's enabled MCP servers into the file the CLIs take with <c>--mcp-config</c>,
/// from <c>build_mcp_config</c>.
/// </summary>
/// <remarks>
/// The stored definition keeps <c>args</c> and <c>env</c> as the plain text the user typed, so the
/// splitting happens here, at launch time — see <see cref="WorkspaceMcp"/> for why they are not
/// parsed on the way in.
/// </remarks>
internal static class McpConfig
{
    /// <summary>
    /// Regenerates the config file and returns its path, or <see langword="null"/> when the
    /// workspace has no enabled server.
    /// </summary>
    /// <remarks>
    /// Null is meaningful: it is what makes the invocation omit the flag entirely rather than pass an
    /// empty server map, which some CLIs treat as an error rather than as "no servers".
    /// </remarks>
    /// <param name="path">
    /// Where to write it, normally <see cref="AppPaths.WorkspaceMcpConfigFile"/>. Taken as an
    /// argument rather than derived here so this stays testable without writing into the real
    /// application directory.
    /// </param>
    public static string? Write(IReadOnlyList<WorkspaceMcp> servers, string path)
    {
        var enabled = servers.Where(server => server.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return null;
        }

        var map = new JsonObject();
        foreach (var server in enabled)
        {
            map[server.Name] = new JsonObject
            {
                ["command"] = server.Command,
                ["args"] = new JsonArray([.. Args(server.Args).Select(arg => (JsonNode)JsonValue.Create(arg))]),
                ["env"] = Env(server.Env),
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var config = new JsonObject { ["mcpServers"] = map };
        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return path;
    }

    /// <summary>Splits the argument line on whitespace.</summary>
    /// <remarks>
    /// No quote handling, deliberately: 1.7.2 splits on whitespace alone, so an argument
    /// containing a space cannot be expressed. Adding quoting here would let a user save a value the
    /// reference would have split, and the two builds would then disagree about the same row.
    /// </remarks>
    private static string[] Args(string args) =>
        args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Reads <c>KEY=value</c> lines, splitting each on its first <c>=</c>.</summary>
    /// <remarks>
    /// A line with no <c>=</c> is skipped. Both halves are trimmed, so a value that is meant to have
    /// leading or trailing spaces cannot be expressed — again as in 1.7.2.
    /// </remarks>
    private static JsonObject Env(string env)
    {
        var map = new JsonObject();

        foreach (var line in env.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            map[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return map;
    }
}
