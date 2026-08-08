using System.Text.Json;
using CodeFlow.Ipc;

namespace CodeFlow.Update;

/// <summary>
/// The three commands behind the Settings update panel and the corner notice.
/// </summary>
/// <remarks>
/// Updating is driven from the sidecar rather than the webview, because a private repository and
/// an unsigned app rule out an anonymous signed manifest. The keychain already holds a credential
/// that can read the release list, and it lives on this side of the boundary.
/// </remarks>
public static class UpdateCommands
{
    public static CommandRegistry AddUpdateCommands(
        this CommandRegistry registry, HttpClient http, PublishEvent publish, string currentVersion)
    {
        var service = new UpdateService(http, publish, currentVersion);

        return registry
            .Add("update_current_version", (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(
                JsonSerializer.SerializeToUtf8Bytes(currentVersion, UpdateJsonContext.Default.String)))
            .Add("update_check", async (_, ct) => JsonSerializer.SerializeToUtf8Bytes(
                await service.CheckAsync(ct).ConfigureAwait(false),
                UpdateJsonContext.Default.UpdateAvailability))
            .Add("update_download", async (p, ct) => JsonSerializer.SerializeToUtf8Bytes(
                await service.DownloadAsync(Arg(p, "assetUrl"), Arg(p, "assetName"), ct).ConfigureAwait(false),
                UpdateJsonContext.Default.UpdateInstallation));
    }

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");
}
