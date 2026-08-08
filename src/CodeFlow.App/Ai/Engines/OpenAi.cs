using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodeFlow.Ai.Engines;

/// <summary>
/// Any endpoint speaking OpenAI's <c>/v1/chat/completions</c> — OpenAI itself, Azure, OpenRouter,
/// Groq, a local vLLM.
/// </summary>
/// <remarks>
/// <para>
/// The key is read from the OS keychain when the engine is constructed and carried on its
/// <see cref="Transport"/>. That is what keeps the credential invariant structural rather than
/// advisory: it never becomes an invocation field, so there is no path by which it could end up in
/// a subprocess's argv or environment.
/// </para>
/// <para>
/// The counterpart to <see cref="Codex"/>: same vendor, different billing — metered API credits
/// here, a ChatGPT subscription there.
/// </para>
/// </remarks>
public sealed class OpenAi(string apiKey) : IAiEngine
{
    /// <summary>OpenAI's own endpoint.</summary>
    /// <remarks>The Settings field is free text, so pointing it elsewhere is the whole config step.</remarks>
    public const string DefaultEndpoint = "https://api.openai.com/v1";

    /// <summary>
    /// Model id fragments that mark a model as not usable for chat.
    /// </summary>
    /// <remarks>
    /// Excluding known non-chat families rather than allow-listing chat ones is deliberate: it
    /// keeps a model name working the day it ships instead of the day the app is updated.
    /// </remarks>
    private static readonly string[] NonChat =
    [
        "embedding", "tts", "whisper", "transcribe", "dall-e", "moderation", "audio",
        "realtime", "image", "sora", "similarity", "-search-", "-edit-", "davinci",
        "babbage", "curie",
    ];

    public string Id => "openai";

    public string Label => "OpenAI";

    public string DefaultBinary => DefaultEndpoint;

    /// <inheritdoc />
    /// <remarks>
    /// Empty: which model is cheap depends entirely on the endpoint this points at, so the caller
    /// falls back to the configured base model.
    /// </remarks>
    public string CommitMessageModel => string.Empty;

    public Transport Transport => new Transport.OpenAiCompatible(apiKey);

    public bool Agentic => false;

    public bool ResumesSessions => false;

    /// <inheritdoc />
    /// <remarks>Never called — the transport branches before anything subprocess-specific runs.</remarks>
    public ProcessStartInfo BuildCommand(string binary, AiInvocation invocation) =>
        throw new NotSupportedException("openai uses the HTTP transport, not a subprocess");

    /// <inheritdoc />
    /// <remarks>Never called — see <see cref="BuildCommand"/>.</remarks>
    public AiRun Interpret(bool success, string statusLabel, string stdout, string stderr) =>
        throw new NotSupportedException("openai uses the HTTP transport, not stdout interpretation");

    /// <summary>Runs one completion.</summary>
    public static async Task<AiRun> CompleteAsync(
        HttpClient http,
        string baseUrl,
        string apiKey,
        AiInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiRunFailedException("Falta la API key. Añádela en Ajustes › Asistente de IA › Proveedores.");
        }

        var model = invocation.Model.Trim();
        if (model.Length == 0)
        {
            throw new AiRunFailedException("Selecciona un modelo en Ajustes (por ejemplo gpt-5).");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model,
                messages = HttpChat.Messages(invocation),
                stream = false,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AiRunFailedException($"No se pudo conectar con {baseUrl}: {ex.Message}");
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = ErrorDetail(payload);
                var status = (int)response.StatusCode;
                throw new AiRunFailedException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        $"La API key fue rechazada ({status}): {detail}",

                    // Rate limits and exhausted credit both land here. The wording matters: it is
                    // what QuotaSignals matches on to show the friendly banner instead of a raw error.
                    HttpStatusCode.TooManyRequests => $"Rate limit / quota exceeded: {detail}",

                    HttpStatusCode.NotFound => $"El modelo '{model}' no existe en este endpoint: {detail}",
                    _ => $"{status}: {detail}",
                });
            }

            var (text, reported) = ReadCompletion(payload);
            if (text.Length == 0)
            {
                throw new AiRunFailedException("El proveedor no devolvió contenido");
            }

            // No session: each request stands alone, so the caller re-sends the context every turn.
            return new AiRun(text, SessionId: null, reported ?? model);
        }
    }

    /// <summary>
    /// Every chat model the endpoint reports, via <c>GET /models</c>.
    /// </summary>
    /// <remarks>
    /// Throws on failure, so this doubles as the reachability and credential check behind the probe.
    /// Sorted, because the API returns them unordered and the list is long enough that the picker
    /// becomes unusable otherwise.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> FetchModelsAsync(
        HttpClient http, string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AiRunFailedException($"{baseUrl}: {ex.Message}");
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AiRunFailedException($"{baseUrl}: {ErrorDetail(payload)}");
            }

            return ReadModels(payload);
        }
    }

    /// <summary>
    /// Whether a model id can be driven through <c>/chat/completions</c>.
    /// </summary>
    /// <remarks>
    /// <c>/models</c> lists everything the key can reach — embeddings, speech, images, moderation —
    /// and an unfiltered list buries the model the user wants under dozens they cannot use here.
    /// </remarks>
    internal static bool IsChatModel(string id)
    {
        var lower = id.ToLowerInvariant();
        return !NonChat.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>Pulls the reason out of an OpenAI-style error body.</summary>
    /// <remarks>Falls back to the raw text for endpoints that do not follow the convention.</remarks>
    internal static string ErrorDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Not every endpoint answers with JSON, especially a proxy in front of one.
        }

        return body.Trim();
    }

    /// <summary>The reply text and, when the endpoint echoes it, the model it resolved to.</summary>
    /// <remarks>
    /// The echoed id is worth preferring over the configured one: an alias resolves server-side,
    /// so this is the only place the run says what actually answered.
    /// </remarks>
    internal static (string Text, string? Model) ReadCompletion(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var text = root.TryGetProperty("choices", out var choices) &&
                   choices.ValueKind == JsonValueKind.Array &&
                   choices.GetArrayLength() > 0 &&
                   choices[0].TryGetProperty("message", out var message) &&
                   message.TryGetProperty("content", out var content) &&
                   content.ValueKind == JsonValueKind.String
            ? content.GetString()!.Trim()
            : string.Empty;

        var model = root.TryGetProperty("model", out var reported) && reported.ValueKind == JsonValueKind.String
            ? reported.GetString()
            : null;

        return (text, model);
    }

    internal static IReadOnlyList<string> ReadModels(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. data.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null)
            .OfType<string>()
            .Where(IsChatModel)
            .Order(StringComparer.Ordinal)];
    }
}
