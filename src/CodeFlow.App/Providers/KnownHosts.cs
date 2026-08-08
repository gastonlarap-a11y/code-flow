using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Providers;

/// <summary>One connected GitHub host, as the settings screen persists it.</summary>
/// <remarks>
/// The stored value carries a username too, which nothing here needs — only the host matters for
/// deciding whether a remote is GitHub.
/// </remarks>
internal sealed record GitHubConnection(string Host);

/// <summary>
/// The GitHub hosts this app is allowed to recognise, from <c>github_known_hosts</c>.
/// </summary>
/// <remarks>
/// Without the allow-list an Enterprise remote is indistinguishable from any other self-hosted git
/// server, so only configured hosts are recognised and everything else falls back to manual linking.
/// </remarks>
internal static class KnownHosts
{
    /// <summary>The setting the settings screen writes, a JSON list of connected hosts.</summary>
    private const string SettingKey = "github_connections";

    /// <summary>
    /// <c>github.com</c> always, plus every connected Enterprise host.
    /// </summary>
    /// <remarks>
    /// A duplicate is skipped case-insensitively, and the first spelling wins — so <c>github.com</c>
    /// keeps its canonical form even if the setting lists it capitalised. A malformed setting value is
    /// tolerated in silence: the user gets the default host rather than an error on a screen that has
    /// nothing to do with what they were doing.
    /// </remarks>
    public static IReadOnlyList<string> ForGitHub(SqliteConnection connection)
    {
        var hosts = new List<string> { RepoDetection.GitHubCom };

        if (Settings.GetSetting(connection, SettingKey) is not { } raw || string.IsNullOrWhiteSpace(raw))
        {
            return hosts;
        }

        try
        {
            var connections = JsonSerializer.Deserialize(raw, KnownHostsJsonContext.Default.ListGitHubConnection);

            foreach (var host in connections?.Select(c => c.Host) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(host)
                    && !hosts.Any(known => known.Equals(host, StringComparison.OrdinalIgnoreCase)))
                {
                    hosts.Add(host);
                }
            }
        }
        catch (JsonException)
        {
            // Tolerated, as in 1.7.2: a value the settings screen never wrote leaves the
            // default host standing instead of failing the command that happened to need it.
        }

        return hosts;
    }
}

/// <summary>Reads the <c>github_connections</c> setting.</summary>
/// <remarks>
/// snake_case to match how the settings screen writes it, alongside every other stored shape.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<GitHubConnection>))]
internal sealed partial class KnownHostsJsonContext : JsonSerializerContext;
