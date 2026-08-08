using System.Text.Json.Serialization;

namespace CodeFlow.ApiClient;

/// <summary>One line of a connection's transcript.</summary>
/// <param name="Direction"><c>sent</c>, <c>received</c>, <c>system</c> or <c>error</c>.</param>
public sealed record StreamMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("data")] string Data,
    [property: JsonPropertyName("event")] string? Event = null,
    [property: JsonPropertyName("binary")] bool Binary = false);

/// <summary>A change in a connection's state.</summary>
/// <param name="Status"><c>connecting</c>, <c>open</c>, <c>closed</c> or <c>error</c>.</param>
public sealed record StreamStatusEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string Detail = "");

/// <summary>What opening a WebSocket needs.</summary>
/// <param name="PingIntervalMs"><c>0</c> = no automatic pings.</param>
public sealed record WsConnectRequest(
    [property: JsonPropertyName("url")] string Url = "",
    [property: JsonPropertyName("headers")] IReadOnlyList<IReadOnlyList<string>>? Headers = null,
    [property: JsonPropertyName("subprotocols")] IReadOnlyList<string>? Subprotocols = null,
    [property: JsonPropertyName("ping_interval_ms")] long PingIntervalMs = 0,
    [property: JsonPropertyName("options")] NetworkOptions? Options = null)
{
    public IReadOnlyList<IReadOnlyList<string>> HeaderPairs => Headers ?? [];

    public IReadOnlyList<string> Protocols => Subprotocols ?? [];

    public NetworkOptions Transport => Options ?? new NetworkOptions();
}

/// <summary>What opening a Socket.IO connection needs.</summary>
/// <param name="Version"><c>v4</c> (Socket.IO 3/4) or <c>v3</c> (Socket.IO 2).</param>
public sealed record SocketIoConnectRequest(
    [property: JsonPropertyName("url")] string Url = "",
    [property: JsonPropertyName("path")] string Path = "",
    [property: JsonPropertyName("namespace")] string Namespace = "/",
    [property: JsonPropertyName("version")] string Version = "v4",
    [property: JsonPropertyName("headers")] IReadOnlyList<IReadOnlyList<string>>? Headers = null,
    [property: JsonPropertyName("auth_json")] string AuthJson = "",
    [property: JsonPropertyName("query")] IReadOnlyList<IReadOnlyList<string>>? Query = null,
    [property: JsonPropertyName("options")] NetworkOptions? Options = null)
{
    public bool IsV4 => !Version.Equals("v3", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<IReadOnlyList<string>> HeaderPairs => Headers ?? [];

    public IReadOnlyList<(string Key, string Value)> QueryPairs =>
        [.. (Query ?? []).Where(q => q.Count >= 2).Select(q => (q[0], q[1]))];

    public NetworkOptions Transport => Options ?? new NetworkOptions();
}

/// <summary>A message published when a connection drops unexpectedly.</summary>
public sealed record MqttLastWill(
    [property: JsonPropertyName("topic")] string Topic = "",
    [property: JsonPropertyName("payload")] string Payload = "",
    [property: JsonPropertyName("qos")] int Qos = 0,
    [property: JsonPropertyName("retain")] bool Retain = false);

/// <summary>What opening an MQTT connection needs.</summary>
/// <param name="Version"><c>5</c> or <c>3.1.1</c>; the two are unrelated stacks in 1.7.2.</param>
public sealed record MqttConnectRequest(
    [property: JsonPropertyName("url")] string Url = "",
    [property: JsonPropertyName("client_id")] string ClientId = "",
    [property: JsonPropertyName("username")] string Username = "",
    [property: JsonPropertyName("password")] string Password = "",
    [property: JsonPropertyName("version")] string Version = "3.1.1",
    [property: JsonPropertyName("keep_alive_secs")] int KeepAliveSecs = 60,
    [property: JsonPropertyName("clean_session")] bool CleanSession = true,
    [property: JsonPropertyName("last_will")] MqttLastWill? LastWill = null,
    [property: JsonPropertyName("options")] NetworkOptions? Options = null)
{
    public bool IsV5 => Version.StartsWith('5');

    public NetworkOptions Transport => Options ?? new NetworkOptions();
}
