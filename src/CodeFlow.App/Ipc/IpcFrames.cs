using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Ipc;

/// <summary>The two logical connections the shell opens to this process.</summary>
/// <remarks>
/// Framing solves message boundaries, not write-side contention: a pipe is one ordered byte
/// stream, so a multi-megabyte diff response and a PTY keystroke echo on the same connection have
/// to serialise and the terminal stutters. Splitting bulk from streaming is what removes that.
/// Two rather than three (which <c>docs/business-rules/90-ambiguities.md</c> proposed): PTY bytes and event lines
/// are both streaming and are already tagged by name in the envelope, so sharing costs at worst a
/// few milliseconds of contention.
/// </remarks>
public enum IpcChannelKind
{
    /// <summary>Unary request/response for all 220 commands, including large payloads.</summary>
    Rpc,

    /// <summary>Server-pushed traffic: PTY output and every event.</summary>
    Stream,
}

/// <summary>The first frame on a new connection, identifying which channel it is.</summary>
public sealed record IpcHello
{
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    /// <summary>
    /// The per-launch token the shell was given on the command line.
    /// </summary>
    /// <remarks>
    /// A Unix domain socket is already restricted by filesystem permissions and a named pipe by
    /// its ACL, so this is not the primary control — it is a cheap guard against another process
    /// on the same machine and same user connecting to a socket it happened to find.
    /// </remarks>
    [JsonPropertyName("token")]
    public required string Token { get; init; }
}

/// <summary>A command invocation: the wire form of the renderer's <c>invoke(name, args)</c>.</summary>
public sealed record IpcRequest
{
    [JsonPropertyName("id")]
    public required long Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>
    /// The renderer's argument object, kept as raw JSON until a handler names its shape.
    /// </summary>
    /// <remarks>
    /// The renderer sends camelCase argument names. The naming policy on
    /// <see cref="IpcJsonContext"/> maps them, so handler parameter records are written in
    /// ordinary C# casing.
    /// </remarks>
    [JsonPropertyName("params")]
    public JsonElement Params { get; init; }
}

/// <summary>The reply to an <see cref="IpcRequest"/>. Exactly one of the two payloads is set.</summary>
public sealed record IpcResponse
{
    [JsonPropertyName("id")]
    public required long Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    /// <summary>
    /// The error message, which reaches the renderer as a rejected promise value.
    /// </summary>
    /// <remarks>
    /// These strings are not free text. Several are parsed by the frontend — the
    /// <c>CHECKOUT_CONFLICT: </c> prefix, <c>QUOTA_EXCEEDED::</c>, <c>RUN_CANCELLED::</c> — and are
    /// listed in <c>docs/business-rules/13-cross-language-contracts.md</c>. A handler that
    /// reformats one of them breaks a feature silently.
    /// </remarks>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>A pushed event. Broadcast to every window, exactly as 1.7.2 does.</summary>
/// <remarks>
/// CodeFlow 1.7.2 emits with <c>app.emit</c> and never <c>emit_to</c>, so every window receives
/// every event and filtering is the frontend's job — each payload carries the id it belongs to.
/// Reproducing the broadcast is a correctness requirement, not a convenience.
/// </remarks>
public sealed record IpcEvent
{
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}
