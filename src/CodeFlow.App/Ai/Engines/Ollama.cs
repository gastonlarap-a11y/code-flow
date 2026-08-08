using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodeFlow.Ai.Engines;

/// <summary>
/// A local Ollama server, reached over HTTP rather than as a subprocess.
/// </summary>
/// <remarks>
/// The only engine with no credential at all, and one of the two that never reach
/// <see cref="BuildCommand"/> or <see cref="Interpret"/> — the transport short-circuits first
/// (<c>AI-003</c>). It is also the only non-agentic one, which is why "fix with AI" and MCP are
/// hidden for it.
/// </remarks>
public sealed class Ollama : IAiEngine
{
    /// <summary>Where Ollama listens out of the box.</summary>
    /// <remarks>Shown as the default in Settings; the field is free text, so a remote host works.</remarks>
    public const string DefaultEndpoint = "http://localhost:11434";

    public string Id => "ollama";

    public string Label => "Ollama";

    public string DefaultBinary => DefaultEndpoint;

    /// <inheritdoc />
    /// <remarks>
    /// Empty: Ollama requires an explicit model on every request, so there is no "let the server
    /// pick" to fall back to and the caller uses the configured base model.
    /// </remarks>
    public string CommitMessageModel => string.Empty;

    public Transport Transport => Transport.Ollama.Instance;

    /// <inheritdoc />
    /// <remarks>A plain completion endpoint runs no tool loop.</remarks>
    public bool Agentic => false;

    /// <inheritdoc />
    /// <remarks>No server-side conversation: each request stands alone.</remarks>
    public bool ResumesSessions => false;

    /// <inheritdoc />
    /// <remarks>Never called — the transport branches before anything subprocess-specific runs.</remarks>
    public ProcessStartInfo BuildCommand(string binary, AiInvocation invocation) =>
        throw new NotSupportedException("ollama uses the HTTP transport, not a subprocess");

    /// <inheritdoc />
    /// <remarks>Never called — see <see cref="BuildCommand"/>.</remarks>
    public AiRun Interpret(bool success, string statusLabel, string stdout, string stderr) =>
        throw new NotSupportedException("ollama uses the HTTP transport, not stdout interpretation");

    /// <summary>Runs one completion against <c>/api/chat</c>.</summary>
    /// <remarks>
    /// Every user-facing string here is Spanish and verbatim from 1.7.2 — they are the copy
    /// the user reads when their local server is not running or the model is not pulled, which is
    /// the overwhelmingly common failure for this provider.
    /// </remarks>
    public static async Task<AiRun> CompleteAsync(
        HttpClient http, string baseUrl, AiInvocation invocation, CancellationToken cancellationToken)
    {
        var model = invocation.Model.Trim();
        if (model.Length == 0)
        {
            throw new AiRunFailedException(
                "Selecciona un modelo de Ollama en Ajustes (por ejemplo qwen2.5-coder o llama3.1).");
        }

        var body = new
        {
            model,
            messages = HttpChat.Messages(invocation),
            stream = false,
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/api/chat", body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AiRunFailedException(
                $"No se pudo conectar a Ollama en {baseUrl}: {ex.Message}. ¿Está corriendo `ollama serve`?");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new AiRunFailedException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? $"El modelo '{model}' no está disponible en Ollama. Descárgalo con `ollama pull {model}`."
                    : $"Ollama devolvió {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var text = ReadContent(payload);
            if (text.Length == 0)
            {
                throw new AiRunFailedException("Ollama no devolvió contenido");
            }

            // Ollama holds no server-side conversation, but the app still needs an id: it is what
            // groups a conversation's turns in the activity log, and entries without one are
            // dropped there. Reuse the caller's so every turn of one chat shares an id; mint one
            // when starting a new conversation.
            var sessionId = invocation.ResumeSessionId ?? $"ollama-{Guid.NewGuid()}";
            return new AiRun(text, sessionId, model);
        }
    }

    /// <summary>
    /// The models installed locally, via <c>/api/tags</c>.
    /// </summary>
    /// <remarks>Throws on failure, so this doubles as the reachability check behind the probe.</remarks>
    public static async Task<IReadOnlyList<string>> FetchTagsAsync(
        HttpClient http, string baseUrl, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AiRunFailedException($"{baseUrl}: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new AiRunFailedException($"{baseUrl}: HTTP {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReadTags(payload);
        }
    }

    internal static string ReadContent(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("message", out var message) &&
               message.TryGetProperty("content", out var content) &&
               content.ValueKind == JsonValueKind.String
            ? content.GetString()!.Trim()
            : string.Empty;
    }

    internal static IReadOnlyList<string> ReadTags(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. models.EnumerateArray()
            .Select(m => m.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null)
            .OfType<string>()];
    }
}
