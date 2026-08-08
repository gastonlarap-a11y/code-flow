using System.Text.Json;
using CodeFlow.Ipc;
using CodeFlow.Security;

namespace CodeFlow.Files;

/// <summary>
/// The two watcher commands and the secret-scan command.
/// </summary>
/// <remarks>
/// Registered together because they are the other half of one slice, not because they share
/// anything: the watcher is a background loop and the scanner is a pure function over a diff.
/// </remarks>
public static class WatcherCommands
{
    public static CommandRegistry AddWatcherCommands(this CommandRegistry registry, RepoWatcher watcher) =>
        registry
            .Add("start_watching", async (p, ct) =>
            {
                await watcher.StartAsync(Arg(p, "repoPath"), ct).ConfigureAwait(false);

                return Unit();
            })
            .Add("stop_watching", async (p, _) =>
            {
                await watcher.StopAsync(Arg(p, "repoPath")).ConfigureAwait(false);

                return Unit();
            })
            .Add("scan_staged_secrets", async (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var hits = await Task.Run(() => SecretScan.ScanStaged(repoPath), ct).ConfigureAwait(false);

                return JsonSerializer.SerializeToUtf8Bytes(hits, SecurityJsonContext.Default.IReadOnlyListSecretHit);
            });

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static ReadOnlyMemory<byte> Unit() => "null"u8.ToArray();
}
