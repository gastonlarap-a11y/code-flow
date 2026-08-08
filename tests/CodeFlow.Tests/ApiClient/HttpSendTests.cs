using System.Net;
using System.Text;
using System.Text.Json;
using CodeFlow.ApiClient;
using CodeFlow.Ipc;
using Xunit;

namespace CodeFlow.Tests.ApiClient;

/// <summary>
/// Sending a request end to end, against a loopback server.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-001</c>–<c>API-024</c>.
/// </summary>
/// <remarks>
/// The vectors cover the pure functions — signing, digest arithmetic, decoding. What they cannot
/// cover is the exchange itself: which headers actually go out, what a redirect does to the method,
/// whether the response cap truncates or fails. A real HTTP server on loopback is the cheapest
/// honest way to assert those, and it keeps the suite offline.
/// </remarks>
public sealed class HttpSendTests
{
    [Fact]
    public async Task A_request_reaches_the_server_and_its_response_comes_back_decoded()
    {
        await using var server = new LoopbackServer(context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            return "{\"ok\":true}"u8.ToArray();
        });

        var response = await Send(new HttpSendRequest { Method = "GET", Url = server.Url });

        Assert.Equal(200, response.Status);
        Assert.Equal("{\"ok\":true}", response.BodyText);
        Assert.Null(response.BodyBase64);
        Assert.Equal(11, response.SizeBytes);
        Assert.Contains(response.Headers, h => h[0] == "content-type" && h[1].StartsWith("application/json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_console_reports_what_actually_went_out()
    {
        await using var server = new LoopbackServer(_ => []);

        var response = await Send(new HttpSendRequest
        {
            Method = "post",
            Url = server.Url,
            Headers = [["X-Custom", "value"]],
            BodyText = "hello",
        });

        // The method is upper-cased and the header name lower-cased, which is what goes on the wire.
        Assert.Equal("POST", response.Sent.Method);
        Assert.Contains(response.Sent.Headers, h => h[0] == "x-custom" && h[1] == "value");
        Assert.Equal("hello", response.Sent.BodyPreview);

        // `VERBATIM`, and mirrored in the frontend: gzip, br, deflate.
        Assert.Contains(response.Sent.Headers, h => h[0] == "accept-encoding" && h[1] == "gzip, br, deflate");
    }

    [Fact]
    public async Task A_cookie_jar_is_sent_as_one_header()
    {
        string? received = null;
        await using var server = new LoopbackServer(context =>
        {
            received = context.Request.Headers["Cookie"];
            return [];
        });

        await Send(new HttpSendRequest
        {
            Url = server.Url,
            Options = new NetworkOptions { Cookies = [["a", "1"], ["b", "2"]] },
        });

        Assert.Equal("a=1; b=2", received);
    }

    /// <summary>
    /// <c>BUG-API-a</c>, reproduced: a 302 downgrades any non-GET method, not only POST.
    /// </summary>
    /// <remarks>
    /// 1.7.2's own comment beside this says "a redirected POST", but the condition it
    /// guards is <c>method != GET &amp;&amp; method != HEAD</c> — so a DELETE arrives as a GET too,
    /// and its body is dropped. Correcting it would change which request the server receives.
    /// </remarks>
    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    public async Task A_302_turns_any_non_get_into_a_bodiless_get(string method)
    {
        var methods = new List<string>();

        await using var server = new LoopbackServer(context =>
        {
            methods.Add(context.Request.HttpMethod);

            if (methods.Count == 1)
            {
                context.Response.StatusCode = 302;
                context.Response.Headers["Location"] = "/landed";
                return [];
            }

            return "landed"u8.ToArray();
        });

        var response = await Send(new HttpSendRequest
        {
            Method = method,
            Url = server.Url,
            BodyText = "payload",
        });

        Assert.Equal([method, "GET"], methods);
        Assert.Equal("landed", response.BodyText);
    }

    [Fact]
    public async Task A_307_keeps_the_method_and_the_body()
    {
        var methods = new List<string>();

        await using var server = new LoopbackServer(context =>
        {
            methods.Add(context.Request.HttpMethod);

            if (methods.Count == 1)
            {
                context.Response.StatusCode = 307;
                context.Response.Headers["Location"] = "/landed";
                return [];
            }

            return [];
        });

        await Send(new HttpSendRequest { Method = "POST", Url = server.Url, BodyText = "payload" });

        Assert.Equal(["POST", "POST"], methods);
    }

    /// <summary>Every hop, final URL last.</summary>
    [Fact]
    public async Task Each_redirect_hop_is_reported()
    {
        var seen = 0;

        await using var server = new LoopbackServer(context =>
        {
            if (++seen <= 2)
            {
                context.Response.StatusCode = 302;
                context.Response.Headers["Location"] = $"/hop{seen}";
                return [];
            }

            return "done"u8.ToArray();
        });

        var response = await Send(new HttpSendRequest { Url = server.Url });

        Assert.Equal(2, response.Redirects.Count);
        Assert.EndsWith("/hop2", response.Redirects[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirects_can_be_refused_altogether()
    {
        await using var server = new LoopbackServer(context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers["Location"] = "/elsewhere";
            return [];
        });

        var response = await Send(new HttpSendRequest
        {
            Url = server.Url,
            Options = new NetworkOptions { FollowRedirects = false },
        });

        Assert.Equal(302, response.Status);
        Assert.Empty(response.Redirects);
    }

    [Fact]
    public async Task Too_many_redirects_says_how_many_were_allowed()
    {
        await using var server = new LoopbackServer(context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers["Location"] = "/again";
            return [];
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(new HttpSendRequest
        {
            Url = server.Url,
            Options = new NetworkOptions { MaxRedirects = 2 },
        }));

        Assert.Contains("more than 2 redirects", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>API-020</c>: the response cap truncates, it does not fail.
    /// </summary>
    /// <remarks>
    /// The first megabyte of a large download is worth showing; an error in its place is not.
    /// </remarks>
    [Fact]
    public async Task A_body_past_the_cap_is_truncated_rather_than_refused()
    {
        await using var server = new LoopbackServer(_ => Encoding.UTF8.GetBytes(new string('x', 10_000)));

        var response = await Send(new HttpSendRequest
        {
            Url = server.Url,
            Options = new NetworkOptions { MaxResponseBytes = 100 },
        });

        Assert.Equal(200, response.Status);
        Assert.Equal(100, response.SizeBytes);
        Assert.Equal(new string('x', 100), response.BodyText);
    }

    [Fact]
    public async Task A_binary_response_comes_back_as_base64()
    {
        await using var server = new LoopbackServer(context =>
        {
            context.Response.ContentType = "image/png";
            return [0x89, 0x50, 0x4E, 0x47];
        });

        var response = await Send(new HttpSendRequest { Url = server.Url });

        Assert.Equal(string.Empty, response.BodyText);
        Assert.Equal("iVBORw==", response.BodyBase64);
    }

    [Fact]
    public async Task A_set_cookie_header_reaches_the_caller_parsed()
    {
        await using var server = new LoopbackServer(context =>
        {
            context.Response.Headers["Set-Cookie"] = "sid=abc; Path=/app; HttpOnly";
            return [];
        });

        var cookie = Assert.Single((await Send(new HttpSendRequest { Url = server.Url })).SetCookies);

        Assert.Equal("sid", cookie.Name);
        Assert.Equal("abc", cookie.Value);
        Assert.Equal("/app", cookie.Path);
        Assert.True(cookie.HttpOnly);
    }

    /// <summary>Digest is a round trip: unauthenticated, then again with the response.</summary>
    [Fact]
    public async Task A_digest_challenge_is_answered_on_a_second_send()
    {
        var authorizations = new List<string?>();

        await using var server = new LoopbackServer(context =>
        {
            authorizations.Add(context.Request.Headers["Authorization"]);

            if (authorizations.Count == 1)
            {
                context.Response.StatusCode = 401;
                context.Response.Headers["WWW-Authenticate"] =
                    "Digest realm=\"test\", nonce=\"abc123\", qop=\"auth\", algorithm=MD5";
                return [];
            }

            return "allowed"u8.ToArray();
        });

        var response = await Send(new HttpSendRequest
        {
            Url = server.Url,
            Auth = new BackendAuth { Kind = "digest", Username = "user", Password = "pass" },
        });

        Assert.Equal("allowed", response.BodyText);
        Assert.Null(authorizations[0]);
        Assert.StartsWith("Digest username=\"user\"", authorizations[1]!, StringComparison.Ordinal);
        Assert.Contains("nonce=\"abc123\"", authorizations[1]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_with_no_digest_challenge_says_the_handshake_cannot_continue()
    {
        await using var server = new LoopbackServer(context =>
        {
            context.Response.StatusCode = 401;
            return [];
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(new HttpSendRequest
        {
            Url = server.Url,
            Auth = new BackendAuth { Kind = "digest", Username = "user", Password = "pass" },
        }));

        Assert.Contains("no 'WWW-Authenticate: Digest' challenge", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_request_carries_its_authorization_to_the_server()
    {
        string? authorization = null;
        await using var server = new LoopbackServer(context =>
        {
            authorization = context.Request.Headers["Authorization"];
            return [];
        });

        await Send(new HttpSendRequest
        {
            Url = server.Url,
            Auth = new BackendAuth
            {
                Kind = "awsv4",
                AccessKey = "AKIDEXAMPLE",
                SecretKey = "secret",
                Region = "us-east-1",
                Service = "service",
            },
        });

        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", authorization!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftp://example.test/x", "only http and https can be sent here")]
    [InlineData("not a url", "is not a valid URL")]
    public async Task A_url_this_cannot_send_says_why(string url, string expected)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Send(new HttpSendRequest { Url = url }));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_method_that_is_not_one_says_so()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Send(new HttpSendRequest { Method = "GET POST", Url = "https://example.test" }));

        Assert.Equal("'GET POST' is not a valid HTTP method", failure.Message);
    }

    // ---------- cancellation ----------

    [Fact]
    public async Task A_tracked_request_can_be_cancelled_while_it_is_in_flight()
    {
        using var started = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);

        await using var server = new LoopbackServer(_ =>
        {
            started.Release();
            release.Wait(TimeSpan.FromSeconds(10));
            return [];
        });

        using var registry = new ApiRegistry();
        var commands = new CommandRegistry().AddApiHttpCommands(registry);
        Assert.True(commands.TryGet("api_send_http_tracked", out var send));
        Assert.True(commands.TryGet("api_cancel_http", out var cancel));

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "run-1",
            request = new { url = server.Url },
        }));

        var inFlight = send(arguments.RootElement, TestContext.Current.CancellationToken).AsTask();

        // Whichever happens first: the server sees the request, or the send fails and says why —
        // waiting only on the semaphore would report a timeout in place of the real error.
        var reached = started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (await Task.WhenAny(inFlight, reached) == inFlight)
        {
            await inFlight;
            Assert.Fail("the send completed before the server ever saw it");
        }

        Assert.True(await reached);

        using var cancelArguments = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "run-1" }));
        await cancel(cancelArguments.RootElement, TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => inFlight);
        Assert.Equal("Request cancelled", failure.Message);

        release.Release();
    }

    /// <summary>
    /// Cancelling an id nobody registered is not an error.
    /// </summary>
    /// <remarks>
    /// The request may have finished between the user pressing stop and the command arriving. That
    /// race is the normal case, not a fault.
    /// </remarks>
    [Fact]
    public async Task Cancelling_a_request_that_already_finished_is_not_an_error()
    {
        using var registry = new ApiRegistry();
        var commands = new CommandRegistry().AddApiHttpCommands(registry);
        Assert.True(commands.TryGet("api_cancel_http", out var cancel));

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "never-registered" }));
        var reply = await cancel(arguments.RootElement, TestContext.Current.CancellationToken);

        Assert.Equal("null", Encoding.UTF8.GetString(reply.Span));
    }

    /// <summary>
    /// A file body releases its handle when the send finishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BuildAsync</c> opens the file and hands the stream to an <c>HttpRequestMessage</c>, and
    /// <c>HttpClient</c> does not take ownership of either. Until the message is disposed the file
    /// stays open — on Windows that means the file the user just uploaded is locked, for however
    /// long it takes a finalizer to run.
    /// </para>
    /// <para>
    /// The assertion is <c>File.Delete</c>, which is what an operating system that enforces sharing
    /// will refuse. On Unix it always succeeds and this test proves only that the send worked; the
    /// suite runs on Windows in CI, which is where the assertion has teeth.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_file_body_does_not_stay_open_after_the_send()
    {
        await using var server = new LoopbackServer(_ => []);

        var path = Path.Combine(Path.GetTempPath(), $"codeflow-upload-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        try
        {
            var response = await Send(new HttpSendRequest
            {
                Method = "POST",
                Url = server.Url,
                BodyFile = path,
            });

            Assert.Equal(200, response.Status);

            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Every redirect hop reopens the file, and every hop closes it again.
    /// </summary>
    /// <remarks>
    /// The exchange loop rebuilds the request from scratch on each hop — a redirected upload
    /// therefore opens the file once per hop, not once per send. A fix scoped to "dispose the last
    /// request" would leave every intermediate hop leaking, which is why this case is separate.
    /// </remarks>
    [Fact]
    public async Task A_redirected_file_body_leaves_no_hop_open()
    {
        var hop = 0;
        await using var server = new LoopbackServer(context =>
        {
            if (Interlocked.Increment(ref hop) == 1)
            {
                // 307 keeps the method and the body, so the second hop reopens the file.
                context.Response.StatusCode = 307;
                context.Response.Headers["Location"] = context.Request.Url!.GetLeftPart(UriPartial.Authority) + "/final";
                return [];
            }

            context.Response.StatusCode = 200;
            return [];
        });

        var path = Path.Combine(Path.GetTempPath(), $"codeflow-upload-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        try
        {
            var response = await Send(new HttpSendRequest
            {
                Method = "POST",
                Url = server.Url,
                BodyFile = path,
            });

            Assert.Equal(200, response.Status);
            Assert.Equal(2, hop);

            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static Task<HttpResponse> Send(HttpSendRequest request) =>
        HttpSend.SendAsync(request, TestContext.Current.CancellationToken);

    /// <summary>A real HTTP server on loopback, so the transport is exercised rather than mocked.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;
        private readonly CancellationTokenSource _stopping = new();

        public LoopbackServer(Func<HttpListenerContext, byte[]> respond)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";

            _listener.Prefixes.Add(Url);
            _listener.Start();

            _serving = Task.Run(async () =>
            {
                while (!_stopping.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    try
                    {
                        var body = respond(context);
                        context.Response.ContentLength64 = body.Length;
                        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
                    {
                        // The client went away — which is exactly what the cancellation test does.
                    }
                    finally
                    {
                        context.Response.Close();
                    }
                }
            });
        }

        public string Url { get; }

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            return port;
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Close();

            try
            {
                await _serving.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                // Stopping is how this loop ends.
            }

            _stopping.Dispose();
        }
    }
}
