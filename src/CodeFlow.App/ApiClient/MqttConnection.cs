using System.Net.Security;
using System.Text;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace CodeFlow.ApiClient;

/// <summary>
/// One MQTT connection.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-040</c>–<c>API-048</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never run against a real broker.</b> There is none on this machine and none in CI, so what
/// the tests cover is the endpoint parsing, the client id and the QoS clamp — the parts with
/// extracted vectors — and not a single byte of the protocol. The README says so.
/// </para>
/// <para>
/// One structural difference from 1.7.2, and it removes code rather than adding it: the
/// reference drives 3.1.1 and 5.0 through two unrelated <c>rumqttc</c> stacks and needs a
/// two-task architecture because its event loop is not cancel-safe. MQTTnet covers both versions
/// behind one client whose receive path already is, so there is one code path here where there
/// were two.
/// </para>
/// <para>
/// <c>DIVERGENCE-API-a</c> still holds: automatic reconnection is off, deliberately.
/// </para>
/// </remarks>
internal sealed class MqttConnection(string id, StreamRegistry registry, MqttConnectRequest request)
    : IStreamConnection
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await registry.PublishStatusAsync(new StreamStatusEvent(id, "connecting")).ConfigureAwait(false);

        var endpoint = MqttEndpoint.Parse(request.Url);

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(endpoint.Host, endpoint.Port)
            .WithClientId(MqttEndpoint.ResolveClientId(request.ClientId))
            .WithProtocolVersion(request.IsV5 ? MqttProtocolVersion.V500 : MqttProtocolVersion.V311)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(Math.Max(request.KeepAliveSecs, 1)))
            .WithCleanSession(request.CleanSession);

        if (endpoint.Tls)
        {
            builder = builder.WithTlsOptions(tls => tls
                .UseTls()
                // BUG-API-d, fixed after parity. 1.7.2's MQTT verifier skips TLS signature
                // verification entirely when verify_ssl is off, and this reproduced it as
                // `!VerifySsl || true` — which is `true` unconditionally, so no certificate was ever
                // checked, verify_ssl on or off. A user who left the default alone had no transport
                // security at all and nothing said so.
                //
                // Now it matches the WebSocket's verifier, which the bug report names as the model of
                // the three: with verify_ssl on, the platform's own answer stands; with it off, the
                // two policy errors a self-signed staging broker actually produces are accepted and
                // nothing else is.
                .WithCertificateValidationHandler(args => AcceptCertificate(args, request.Transport.VerifySsl)));
        }

        if (request.Username.Length > 0)
        {
            builder = builder.WithCredentials(request.Username, request.Password);
        }

        // Conditional on a non-empty topic: a will with nowhere to go is not a will, and some
        // brokers refuse the CONNECT outright rather than ignoring it.
        if (request.LastWill is { Topic.Length: > 0 } will)
        {
            builder = builder
                .WithWillTopic(will.Topic)
                .WithWillPayload(Encoding.UTF8.GetBytes(will.Payload))
                .WithWillQualityOfServiceLevel((MqttQualityOfServiceLevel)MqttEndpoint.ClampQos(will.Qos))
                .WithWillRetain(will.Retain);
        }

        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;

        try
        {
            await _client.ConnectAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is MQTTnet.Exceptions.MqttCommunicationException or OperationCanceledException)
        {
            await registry.PublishStatusAsync(new StreamStatusEvent(id, "error", e.Message)).ConfigureAwait(false);
            registry.Forget(id);

            throw new InvalidOperationException($"could not connect to {request.Url}: {e.Message}");
        }

        await registry.PublishStatusAsync(new StreamStatusEvent(id, "open", $"{endpoint.Host}:{endpoint.Port}"))
            .ConfigureAwait(false);
    }

    /// <summary>Publishing is what <c>SendAsync</c> means for MQTT; the topic rides in the payload.</summary>
    public Task SendAsync(string payload, bool binary) =>
        throw new InvalidOperationException("an MQTT connection publishes to a topic; use api_mqtt_publish");

    public async Task PublishAsync(string topic, string payload, int qos, bool retain)
    {
        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)MqttEndpoint.ClampQos(qos))
            .WithRetainFlag(retain)
            .Build()).ConfigureAwait(false);

        await registry.PublishMessageAsync(new StreamMessage(id, "sent", payload, topic)).ConfigureAwait(false);
    }

    public async Task SubscribeAsync(string topic, int qos)
    {
        await _client.SubscribeAsync(topic, (MqttQualityOfServiceLevel)MqttEndpoint.ClampQos(qos))
            .ConfigureAwait(false);

        await registry.PublishMessageAsync(new StreamMessage(id, "system", $"subscribed to {topic}"))
            .ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(string topic)
    {
        await _client.UnsubscribeAsync(topic).ConfigureAwait(false);

        await registry.PublishMessageAsync(new StreamMessage(id, "system", $"unsubscribed from {topic}"))
            .ConfigureAwait(false);
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e) =>
        registry.PublishMessageAsync(new StreamMessage(
            id, "received", Encoding.UTF8.GetString(e.ApplicationMessage.Payload), e.ApplicationMessage.Topic));

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        // Nothing reconnects. The status says what happened and the user decides.
        registry.Forget(id);

        return registry.PublishStatusAsync(
            new StreamStatusEvent(id, "closed", e.Reason.ToString()));
    }

    public async ValueTask DisposeAsync()
    {
        _client.ApplicationMessageReceivedAsync -= OnMessageAsync;
        _client.DisconnectedAsync -= OnDisconnectedAsync;

        try
        {
            await _client.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is MQTTnet.Exceptions.MqttCommunicationException or ObjectDisposedException)
        {
            // Closing a connection that is already gone is not a failure.
        }

        _client.Dispose();
    }

    /// <summary>
    /// Whether to accept the broker's certificate. <c>BUG-API-d</c>: the rule is
    /// <see cref="StreamTlsPolicy"/>'s, shared with the WebSocket rather than written again here.
    /// </summary>
    private static bool AcceptCertificate(MqttClientCertificateValidationEventArgs args, bool verifySsl) =>
        StreamTlsPolicy.Accepts(args.SslPolicyErrors, verifySsl);
}
