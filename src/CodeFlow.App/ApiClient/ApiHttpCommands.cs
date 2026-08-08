using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Ipc;

namespace CodeFlow.ApiClient;

/// <summary>
/// The transport commands: sending a request, cancelling one, and the
/// two file helpers that read a payload off disk.
/// </summary>
/// <remarks>
/// <c>api_pick_file</c> and <c>api_save_file</c> are deliberately absent. They open native dialogs,
/// which belong to the process that owns the window — the same call slice 3 made for
/// <c>pick_folder</c>. They are served by <c>renderer/src/lib/bridge/dialog.ts</c> over the shell's
/// existing dialog bridge, and their renderer wrappers keep their signatures.
/// </remarks>
public static class ApiHttpCommands
{
    public static CommandRegistry AddApiHttpCommands(this CommandRegistry registry, ApiRegistry inFlight) =>
        registry
            .Add("api_send_http", async (p, ct) =>
            {
                var response = await HttpSend.SendAsync(Request(p), ct).ConfigureAwait(false);

                return Json(response);
            })
            .Add("api_send_http_tracked", async (p, ct) =>
            {
                var id = Arg(p, "id");
                var request = Request(p);

                var source = inFlight.Track(id, ct);

                try
                {
                    return Json(await HttpSend.SendAsync(request, source.Token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // The user pressed stop. Not a transport failure, and the message is what the
                    // panel shows in place of a response.
                    throw new InvalidOperationException("Request cancelled");
                }
                finally
                {
                    inFlight.Release(id, source);
                }
            })
            .Add("api_cancel_http", (p, _) =>
            {
                inFlight.Cancel(Arg(p, "id"));

                return ValueTask.FromResult(Unit());
            })

            // ---------- reading a payload off disk ----------

            .Add("api_read_file_base64", (p, ct) =>
            {
                var path = Arg(p, "path");

                return Run(async () =>
                {
                    var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

                    return new ReadFileResult(Convert.ToBase64String(bytes), MimeOf(path), bytes.Length);
                }, ApiHttpJsonContext.Default.ReadFileResult);
            })
            .Add("api_read_text_file", (p, ct) =>
            {
                var path = Arg(p, "path");

                return Run(
                    async () => await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
                    ApiHttpJsonContext.Default.String);
            });

    /// <summary>
    /// A file's media type, by extension.
    /// </summary>
    /// <remarks>
    /// The table is 1.7.2's, and <c>XLANG-009</c> lists it as duplicated on the frontend —
    /// so the fallback matters as much as the entries: an unknown extension is
    /// <c>application/octet-stream</c>, never a guess.
    /// </remarks>
    internal static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".txt" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".csv" => "text/csv",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    private static async ValueTask<ReadOnlyMemory<byte>> Run<T>(
        Func<Task<T>> work, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        var result = await work().ConfigureAwait(false);

        return JsonSerializer.SerializeToUtf8Bytes(result, type);
    }

    private static ReadOnlyMemory<byte> Json(HttpResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, ApiHttpJsonContext.Default.HttpResponse);

    private static HttpSendRequest Request(JsonElement parameters) =>
        parameters.TryGetProperty("request", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize(ApiHttpJsonContext.Default.HttpSendRequest)
              ?? throw new ArgumentException("parameter 'request' deserialised to null")
            : throw new ArgumentException("missing required parameter 'request'");

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static ReadOnlyMemory<byte> Unit() => "null"u8.ToArray();
}

/// <summary>What <c>api_read_file_base64</c> answers.</summary>
public sealed record ReadFileResult(string Base64, string Mime, long Size);

/// <summary>The transport's wire types.</summary>
/// <remarks>
/// snake_case, like every other payload this sidecar returns — <c>body_text</c>,
/// <c>set_cookies</c>, <c>first_byte_ms</c>. <see cref="HttpSendRequest"/> travels the other way
/// and carries its own <c>[JsonPropertyName]</c> attributes, because the renderer builds it as a
/// whole object rather than as a camelCase argument list.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HttpSendRequest))]
[JsonSerializable(typeof(HttpResponse))]
[JsonSerializable(typeof(ReadFileResult))]
[JsonSerializable(typeof(string))]
internal sealed partial class ApiHttpJsonContext : JsonSerializerContext;
