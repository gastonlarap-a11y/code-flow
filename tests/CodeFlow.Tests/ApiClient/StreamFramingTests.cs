using System.Net.Security;
using System.Text.Json;
using CodeFlow.ApiClient;
using CodeFlow.Tests.TestVectors;
using Xunit;

namespace CodeFlow.Tests.ApiClient;

/// <summary>
/// The framing and parsing the three streaming protocols run on, against every extracted vector.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-025</c>–<c>API-048</c>.
/// </summary>
/// <remarks>
/// These are the eighteen cases across <c>ws.vectors.json</c>, <c>socketio.vectors.json</c> and
/// <c>mqtt.vectors.json</c>. What they cover is everything that can be decided without a peer —
/// which for Socket.IO is most of it, because 1.7.2 hand-rolls the framing rather than
/// taking a client library.
/// </remarks>
public sealed class StreamFramingTests
{
    // ---------- WebSocket ----------

    [Fact]
    public void An_http_url_is_rewritten_to_its_websocket_scheme()
    {
        var testCase = Vector("ws.vectors.json", "normalize-scheme");

        var results = testCase.Input.GetProperty("urls").EnumerateArray()
            .Select(u => WebSocketStream.NormalizeScheme(u.GetString()!));

        Assert.Equal(Strings(testCase.Expected, "results"), results);
    }

    /// <summary>
    /// The first header of a name replaces; repeats append.
    /// </summary>
    /// <remarks>
    /// That distinction is the difference between one <c>Origin</c> and two — which some servers
    /// reject outright — and between one <c>X-Tag</c> and the several the caller meant.
    /// </remarks>
    [Fact]
    public void Repeated_headers_are_kept_and_singles_replace()
    {
        var testCase = Vector("ws.vectors.json", "header-merge-first-replaces-repeats-append");

        var headers = testCase.Input.GetProperty("headers").EnumerateArray()
            .Select(pair => (IReadOnlyList<string>)[pair[0].GetString()!, pair[1].GetString()!])
            .ToList();

        var origin = WebSocketStream.MergedValues(headers, "Origin");
        Assert.Equal(testCase.Expected.GetProperty("origin_header_count").GetInt32(), origin.Count);
        Assert.Equal(testCase.Expected.GetProperty("origin_header_value").GetString(), origin[0]);

        Assert.Equal(Strings(testCase.Expected, "x_tag_values"), WebSocketStream.MergedValues(headers, "X-Tag"));
    }

    // ---------- Socket.IO framing ----------

    [Theory]
    [InlineData("frame-root-namespace-no-segment")]
    [InlineData("frame-named-namespace-comma-terminated")]
    public void A_frame_carries_its_namespace_the_way_the_vector_says(string caseId)
    {
        var testCase = Vector("socketio.vectors.json", caseId);

        var results = testCase.Input.GetProperty("calls").EnumerateArray().Select(call =>
            SocketIoFraming.MessageFrame(
                KindOf(call.GetProperty("kind").GetString()!),
                call.GetProperty("namespace").GetString()!,
                call.GetProperty("body").GetString()!));

        Assert.Equal(Strings(testCase.Expected, "results"), results);
    }

    /// <summary>Socket.IO 2 has no auth field, and an empty object is not auth.</summary>
    [Fact]
    public void Connect_auth_travels_only_on_v4_and_only_when_there_is_some()
    {
        var testCase = Vector("socketio.vectors.json", "connect-auth-only-on-v4-and-non-empty");

        var results = testCase.Input.GetProperty("calls").EnumerateArray().Select(call =>
            SocketIoFraming.MessageFrame(
                SocketIoFraming.Connect,
                call.GetProperty("namespace").GetString()!,
                SocketIoFraming.ConnectBody(
                    call.GetProperty("auth_json").GetString()!,
                    call.GetProperty("v4").GetBoolean())));

        Assert.Equal(Strings(testCase.Expected, "results"), results);
    }

    [Fact]
    public void One_argument_keeps_its_shape_through_a_round_trip()
    {
        var testCase = Vector("socketio.vectors.json", "event-one-argument-keeps-shape");

        var frame = SocketIoFraming.MessageFrame(
            SocketIoFraming.Event,
            "/",
            SocketIoFraming.EventArgs(
                testCase.Input.GetProperty("event").GetString()!,
                testCase.Input.GetProperty("payload_json").GetString()!));

        Assert.Equal(testCase.Expected.GetProperty("frame").GetString(), frame);

        var packet = SocketIoFraming.DecodePacket(frame[1..]);
        Assert.Equal(SocketIoFraming.Event, packet.Kind);
        Assert.Equal(testCase.Expected.GetProperty("decoded_packet").GetProperty("namespace").GetString(), packet.Namespace);

        var (name, payload) = SocketIoFraming.SplitEvent(packet.Data);
        var expected = Strings(testCase.Expected, "split_event").ToArray();
        Assert.Equal(expected[0], name);
        Assert.Equal(expected[1], payload);
    }

    [Fact]
    public void Several_arguments_become_an_array()
    {
        var testCase = Vector("socketio.vectors.json", "event-several-arguments-become-array");

        var packet = SocketIoFraming.DecodePacket(testCase.Input.GetProperty("raw_packet_body").GetString()!);
        Assert.Equal(testCase.Expected.GetProperty("namespace").GetString(), packet.Namespace);

        var (name, payload) = SocketIoFraming.SplitEvent(packet.Data);
        var expected = Strings(testCase.Expected, "split_event").ToArray();
        Assert.Equal(expected[0], name);
        Assert.Equal(expected[1], payload);
    }

    [Fact]
    public void A_bare_string_payload_stays_json()
    {
        var testCase = Vector("socketio.vectors.json", "event-bare-string-payload-stays-json");
        var call = testCase.Input.GetProperty("event_args_call");

        Assert.Equal(
            testCase.Expected.GetProperty("event_args_result").GetString(),
            SocketIoFraming.EventArgs(
                call.GetProperty("event").GetString()!, call.GetProperty("payload_json").GetString()!));

        var (name, payload) = SocketIoFraming.SplitEvent(testCase.Input.GetProperty("split_event_call").GetString()!);
        var expected = Strings(testCase.Expected, "split_event_result").ToArray();
        Assert.Equal(expected[0], name);
        Assert.Equal(expected[1], payload);
    }

    [Fact]
    public void An_event_with_no_payload_sends_only_its_name()
    {
        var testCase = Vector("socketio.vectors.json", "event-no-payload-sends-only-name");
        var call = testCase.Input.GetProperty("event_args_call");

        Assert.Equal(
            testCase.Expected.GetProperty("event_args_result").GetString(),
            SocketIoFraming.EventArgs(
                call.GetProperty("event").GetString()!, call.GetProperty("payload_json").GetString()!));

        var (name, payload) = SocketIoFraming.SplitEvent(testCase.Input.GetProperty("split_event_call").GetString()!);
        var expected = Strings(testCase.Expected, "split_event_result").ToArray();
        Assert.Equal(expected[0], name);
        Assert.Equal(expected[1], payload);
    }

    /// <summary>
    /// An unparseable payload is refused rather than sent as a string.
    /// </summary>
    /// <remarks>
    /// Sending it would silently change the argument's type on the other side, which is the kind of
    /// bug that gets blamed on the server.
    /// </remarks>
    [Fact]
    public void A_payload_that_is_not_json_is_refused()
    {
        var testCase = Vector("socketio.vectors.json", "event-payload-must-be-json");

        Assert.True(testCase.Expected.GetProperty("error").GetBoolean());
        Assert.Throws<InvalidOperationException>(() => SocketIoFraming.EventArgs(
            testCase.Input.GetProperty("event").GetString()!,
            testCase.Input.GetProperty("payload_json").GetString()!));
    }

    [Fact]
    public void The_engine_opcodes_decode_and_encode()
    {
        var testCase = Vector("socketio.vectors.json", "engine-opcodes");
        var expected = testCase.Expected.GetProperty("decode_results").EnumerateArray().ToArray();
        var calls = testCase.Input.GetProperty("decode_calls").EnumerateArray().ToArray();

        for (var i = 0; i < calls.Length; i++)
        {
            var decoded = SocketIoFraming.DecodeEngine(calls[i].GetString()!);

            if (expected[i].ValueKind == JsonValueKind.Null)
            {
                Assert.Null(decoded);
                continue;
            }

            Assert.NotNull(decoded);

            // The vector names each opcode; what matters is the body it carries with it.
            var (name, body) = expected[i].ValueKind == JsonValueKind.String
                ? (expected[i].GetString()!, string.Empty)
                : expected[i].EnumerateObject().Select(p => (p.Name, p.Value.GetString()!)).Single();

            Assert.Equal(name[0], decoded.Value.Kind switch
            {
                '0' => 'O',
                '1' => 'C',
                '2' => 'P',
                '3' => 'P',
                _ => 'M',
            });
            Assert.Equal(body, decoded.Value.Body);
        }

        Assert.Equal(testCase.Expected.GetProperty("engine_frame_result").GetString(), SocketIoFraming.EngineFrame('P'));
    }

    [Fact]
    public void An_ack_and_a_binary_packet_carry_their_metadata()
    {
        var testCase = Vector("socketio.vectors.json", "ack-and-binary-metadata");

        var ack = SocketIoFraming.DecodePacket(testCase.Input.GetProperty("ack_packet").GetString()!);
        var expectedAck = testCase.Expected.GetProperty("ack");
        Assert.Equal(SocketIoFraming.Ack, ack.Kind);
        Assert.Equal(expectedAck.GetProperty("ack_id").GetInt32(), ack.AckId);
        Assert.Equal(expectedAck.GetProperty("data").GetString(), ack.Data);

        var binary = SocketIoFraming.DecodePacket(testCase.Input.GetProperty("binary_packet").GetString()!);
        var expectedBinary = testCase.Expected.GetProperty("binary");
        Assert.Equal(SocketIoFraming.BinaryEvent, binary.Kind);
        Assert.Equal(expectedBinary.GetProperty("attachments").GetInt32(), binary.Attachments);
        Assert.Equal(expectedBinary.GetProperty("namespace").GetString(), binary.Namespace);
        Assert.Equal(expectedBinary.GetProperty("ack_id").GetInt32(), binary.AckId);
    }

    [Fact]
    public void A_namespace_with_nothing_after_it_still_parses()
    {
        var testCase = Vector("socketio.vectors.json", "namespace-without-payload");

        var packet = SocketIoFraming.DecodePacket(testCase.Input.GetProperty("raw_packet_body").GetString()!);

        Assert.Equal(SocketIoFraming.Connect, packet.Kind);
        Assert.Equal(testCase.Expected.GetProperty("namespace").GetString(), packet.Namespace);
        Assert.Equal(testCase.Expected.GetProperty("data").GetString(), packet.Data);
    }

    [Fact]
    public void The_handshake_url_upgrades_its_scheme_and_keeps_the_query()
    {
        var testCase = Vector("socketio.vectors.json", "handshake-url-scheme-upgrade-and-query");
        var expected = testCase.Expected.GetProperty("results").EnumerateArray().ToArray();
        var calls = testCase.Input.GetProperty("calls").EnumerateArray().ToArray();

        for (var i = 0; i < calls.Length; i++)
        {
            var call = calls[i];
            var query = call.GetProperty("query").EnumerateArray()
                .Select(q => (q[0].GetString()!, q[1].GetString()!))
                .ToList();

            string Build() => SocketIoFraming.HandshakeUrl(
                call.GetProperty("url").GetString()!,
                call.GetProperty("path").GetString()!,
                call.GetProperty("eio").GetInt32(),
                query);

            if (expected[i].ValueKind == JsonValueKind.Object)
            {
                Assert.True(expected[i].GetProperty("error").GetBoolean());
                Assert.Throws<InvalidOperationException>(Build);
            }
            else
            {
                Assert.Equal(expected[i].GetString(), Build());
            }
        }
    }

    // ---------- MQTT ----------

    [Fact]
    public void A_broker_address_resolves_its_scheme_and_default_port()
    {
        var testCase = Vector("mqtt.vectors.json", "parse-endpoint-schemes-and-default-ports");
        var expected = testCase.Expected.GetProperty("results").EnumerateArray().ToArray();
        var urls = testCase.Input.GetProperty("urls").EnumerateArray().ToArray();

        for (var i = 0; i < urls.Length; i++)
        {
            var endpoint = MqttEndpoint.Parse(urls[i].GetString()!);
            var wanted = expected[i];

            if (wanted.TryGetProperty("host", out var host))
            {
                Assert.Equal(host.GetString(), endpoint.Host);
            }

            Assert.Equal(wanted.GetProperty("port").GetInt32(), endpoint.Port);

            if (wanted.TryGetProperty("tls", out var tls))
            {
                Assert.Equal(tls.GetBoolean(), endpoint.Tls);
            }
        }
    }

    /// <summary>
    /// MQTT over WebSocket is a different transport, so its schemes are refused.
    /// </summary>
    /// <remarks>
    /// Treating <c>ws://</c> as raw TCP would connect to port 8083 and then wait for a broker that
    /// is speaking something else — a hang rather than a failure.
    /// </remarks>
    [Fact]
    public void An_address_this_cannot_reach_is_refused_rather_than_guessed()
    {
        var testCase = Vector("mqtt.vectors.json", "parse-endpoint-rejections");

        Assert.True(testCase.Expected.GetProperty("all_error").GetBoolean());

        foreach (var url in testCase.Input.GetProperty("urls").EnumerateArray())
        {
            Assert.Throws<InvalidOperationException>(() => MqttEndpoint.Parse(url.GetString()!));
        }
    }

    [Fact]
    public void A_client_id_is_kept_when_given_and_generated_when_not()
    {
        var testCase = Vector("mqtt.vectors.json", "client-id-generation");
        var calls = testCase.Input.GetProperty("calls").EnumerateArray().ToArray();

        Assert.Equal(
            testCase.Expected.GetProperty("results")[0].GetString(),
            MqttEndpoint.ResolveClientId(calls[0].GetString()!));

        var generated = MqttEndpoint.ResolveClientId(calls[1].GetString()!);
        Assert.StartsWith("codeflow-", generated, StringComparison.Ordinal);
        Assert.Equal(17, generated.Length);
    }

    /// <summary>Out of range becomes 0, not the nearest valid value.</summary>
    [Fact]
    public void A_quality_of_service_out_of_range_falls_to_at_most_once()
    {
        var testCase = Vector("mqtt.vectors.json", "qos-clamping");

        var results = testCase.Input.GetProperty("clamp_qos_calls").EnumerateArray()
            .Select(q => MqttEndpoint.ClampQos(q.GetInt32()));

        Assert.Equal(
            testCase.Expected.GetProperty("clamp_qos_results").EnumerateArray().Select(q => q.GetInt32()),
            results);
    }

    private static int KindOf(string name) => name switch
    {
        "CONNECT" => SocketIoFraming.Connect,
        "DISCONNECT" => SocketIoFraming.Disconnect,
        "EVENT" => SocketIoFraming.Event,
        "ACK" => SocketIoFraming.Ack,
        _ => throw new InvalidOperationException($"the vector names an unknown packet kind {name}"),
    };

    private static IEnumerable<string> Strings(JsonElement element, string key) =>
        element.GetProperty(key).EnumerateArray().Select(e => e.GetString()!);

    private static FixtureCase Vector(string file, string caseId) =>
        FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, file))
            .SelectMany(f => f.Cases)
            .Single(c => c.Id == caseId);

    // ---------- BUG-API-d: one TLS rule for every streaming protocol ----------

    [Theory]
    [InlineData(SslPolicyErrors.None, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    public void With_verification_on_only_a_clean_certificate_passes(SslPolicyErrors errors, bool accepted)
    {
        // The half that used to be broken outright. MQTT's handler read `!VerifySsl || true`, which
        // is `true` unconditionally: no certificate was checked even with verify_ssl left on its
        // default. A user got no transport security and nothing anywhere said so.
        Assert.Equal(accepted, StreamTlsPolicy.Accepts(errors, verifySsl: true));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void With_verification_off_a_self_signed_staging_certificate_is_accepted(SslPolicyErrors errors)
    {
        // What turning the toggle off is *for*: an issuer nothing trusts, a name that does not match,
        // or both — which is what a staging box with a self-signed certificate produces.
        Assert.True(StreamTlsPolicy.Accepts(errors, verifySsl: false));
    }

    [Fact]
    public void With_verification_off_a_certificate_that_fails_for_any_other_reason_is_still_refused()
    {
        // The line BUG-API-d is actually about. MQTT and gRPC skipped signature verification
        // entirely, so "I trust this staging box" silently became "I accept anybody's certificate".
        // A signature that does not verify is not a misconfiguration — it is someone else's
        // certificate — and no amount of turning verification off should accept it.
        Assert.False(StreamTlsPolicy.Accepts(SslPolicyErrors.RemoteCertificateNotAvailable, verifySsl: false));

        Assert.False(StreamTlsPolicy.Accepts(
            SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateChainErrors,
            verifySsl: false));
    }
}
