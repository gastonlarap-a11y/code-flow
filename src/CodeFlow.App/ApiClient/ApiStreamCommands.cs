using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Ipc;

namespace CodeFlow.ApiClient;

/// <summary>
/// The ten streaming commands: WebSocket, Socket.IO and MQTT.
/// </summary>
public static class ApiStreamCommands
{
    public static CommandRegistry AddApiStreamCommands(this CommandRegistry registry, StreamRegistry streams) =>
        registry

            // ---------- WebSocket ----------

            .Add("api_ws_connect", async (p, ct) =>
            {
                await streams.ConnectWebSocketAsync(
                    Arg(p, "id"), Body(p, StreamCommandJsonContext.Default.WsConnectRequest), ct)
                    .ConfigureAwait(false);

                return Unit();
            })
            .Add("api_ws_send", async (p, _) =>
            {
                await streams.SendAsync(Arg(p, "id"), Arg(p, "payload"), Flag(p, "binary")).ConfigureAwait(false);

                return Unit();
            })

            // ---------- Socket.IO ----------

            .Add("api_socketio_connect", async (p, ct) =>
            {
                await streams.ConnectSocketIoAsync(
                    Arg(p, "id"), Body(p, StreamCommandJsonContext.Default.SocketIoConnectRequest), ct)
                    .ConfigureAwait(false);

                return Unit();
            })
            .Add("api_socketio_emit", async (p, _) =>
            {
                await streams.EmitAsync(Arg(p, "id"), Arg(p, "event"), Arg(p, "payloadJson")).ConfigureAwait(false);

                return Unit();
            })

            // ---------- MQTT ----------

            .Add("api_mqtt_connect", async (p, ct) =>
            {
                await streams.ConnectMqttAsync(
                    Arg(p, "id"), Body(p, StreamCommandJsonContext.Default.MqttConnectRequest), ct)
                    .ConfigureAwait(false);

                return Unit();
            })
            .Add("api_mqtt_publish", async (p, _) =>
            {
                await streams.PublishAsync(
                    Arg(p, "id"), Arg(p, "topic"), Arg(p, "payload"), Number(p, "qos"), Flag(p, "retain"))
                    .ConfigureAwait(false);

                return Unit();
            })
            .Add("api_mqtt_subscribe", async (p, _) =>
            {
                await streams.SubscribeAsync(Arg(p, "id"), Arg(p, "topic"), Number(p, "qos")).ConfigureAwait(false);

                return Unit();
            })
            .Add("api_mqtt_unsubscribe", async (p, _) =>
            {
                await streams.UnsubscribeAsync(Arg(p, "id"), Arg(p, "topic")).ConfigureAwait(false);

                return Unit();
            })

            // ---------- shared ----------

            .Add("api_stream_disconnect", async (p, _) =>
            {
                await streams.DisconnectAsync(Arg(p, "id")).ConfigureAwait(false);

                return Unit();
            });

    private static T Body<T>(JsonElement parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        parameters.TryGetProperty("request", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize(type) ?? throw new ArgumentException("parameter 'request' deserialised to null")
            : throw new ArgumentException("missing required parameter 'request'");

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static int Number(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    /// <summary>
    /// A boolean the renderer may simply omit.
    /// </summary>
    /// <remarks>
    /// Absent means false — <c>binary</c> and <c>retain</c> are both opt-in, and refusing the
    /// command for a missing flag would break the common call.
    /// </remarks>
    private static bool Flag(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static ReadOnlyMemory<byte> Unit() => "null"u8.ToArray();
}

/// <summary>The three connection requests, as the renderer sends them.</summary>
/// <remarks>
/// Each carries its own <c>[JsonPropertyName]</c> attributes and its defaults on a constructor —
/// the source generator does not run property initialisers for members a payload omits, which cost
/// the HTTP transport a NullReferenceException before it was found.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WsConnectRequest))]
[JsonSerializable(typeof(SocketIoConnectRequest))]
[JsonSerializable(typeof(MqttConnectRequest))]
internal sealed partial class StreamCommandJsonContext : JsonSerializerContext;
