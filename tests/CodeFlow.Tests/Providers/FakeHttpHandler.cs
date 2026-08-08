using System.Net;
using System.Text;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// A transport that answers with canned responses and records what it was asked.
/// </summary>
/// <remarks>
/// <para>
/// The first HTTP-mocking seam in this suite. It exists because a provider client is the first thing
/// in the app whose <em>requests</em> are the behaviour under test — the URL, the four headers, the
/// body shape — not just what it does with a reply. The two HTTP AI engines have no equivalent
/// coverage, which is a gap this pattern makes closable later.
/// </para>
/// <para>
/// <b>Queued by default, and routable when order is not a fact.</b> Several client methods make two or
/// three calls in a fixed order and a queue asserts that order for free, so that stays the default. But
/// Azure's diff assembly renders up to six files at a time, so up to twelve requests race: their order
/// is genuinely unspecified, and asserting one would be asserting a coincidence. <see cref="When"/>
/// registers a response by URL fragment for those, and every route survives repeated matching.
/// </para>
/// <para>
/// A call that matches no route and finds nothing queued fails loudly rather than returning an empty
/// 200, which would surface as a confusing deserialisation error instead.
/// </para>
/// <para>
/// Everything mutable here is under one lock. With concurrent requests in play the recording list and
/// the queue are touched from several threads at once, and <see cref="Queue{T}"/> is not thread-safe —
/// which would show up as a flaky test blamed on the client rather than on its double.
/// </para>
/// </remarks>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    /// <summary>One request as it was sent, snapshotted so it survives the response being disposed.</summary>
    internal sealed record Captured(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string[]> Headers,
        string? Body)
    {
        /// <summary>The first value of a header, or null when it was not sent.</summary>
        public string? Header(string name) => Headers.TryGetValue(name, out var values) ? values[0] : null;
    }

    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    private readonly List<(string Fragment, Func<HttpResponseMessage> Respond)> _routes = [];

    private readonly List<Captured> _requests = [];

    private readonly Lock _gate = new();

    /// <summary>Every request this handler received, in the order it received them.</summary>
    /// <remarks>A snapshot: with concurrent requests in flight, handing out the live list would race.</remarks>
    public IReadOnlyList<Captured> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The only request this handler received.</summary>
    public Captured Only
    {
        get
        {
            var requests = Requests;
            return requests.Count == 1
                ? requests[0]
                : throw new InvalidOperationException($"expected exactly one request, got {requests.Count}");
        }
    }

    /// <summary>
    /// Answers every request whose URL contains <paramref name="fragment"/> with this body.
    /// </summary>
    /// <remarks>
    /// For clients whose request order is not part of the contract. Routes are matched in registration
    /// order and never consumed, so one route can serve many identical calls — which is what a diff
    /// fetching the same blob twice needs. Checked before the queue, so a test can route the noisy calls
    /// and still queue the one whose position it wants to pin.
    /// </remarks>
    public FakeHttpHandler When(string fragment, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        lock (_gate)
        {
            _routes.Add((fragment, () => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }));
        }

        return this;
    }

    /// <summary>Answers every request whose URL contains <paramref name="fragment"/> with raw bytes.</summary>
    /// <remarks>Azure serves a file's content as an octet stream, which is not text and may not be UTF-8.</remarks>
    public FakeHttpHandler WhenBytes(string fragment, byte[] body)
    {
        lock (_gate)
        {
            _routes.Add((fragment, () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            }));
        }

        return this;
    }

    /// <summary>How many requests hit URLs containing <paramref name="fragment"/>.</summary>
    /// <remarks>
    /// The honest assertion for a client whose call order is unspecified: how many times it asked, not when.
    /// </remarks>
    public int CountFor(string fragment) =>
        Requests.Count(r => r.Uri.ToString().Contains(fragment, StringComparison.Ordinal));

    public FakeHttpHandler Json(string body) => Respond(HttpStatusCode.OK, body, "application/json");

    public FakeHttpHandler Text(string body) => Respond(HttpStatusCode.OK, body, "text/plain");

    public FakeHttpHandler Respond(HttpStatusCode status, string body = "", string contentType = "application/json")
    {
        lock (_gate)
        {
            _responses.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
        }

        return this;
    }

    /// <summary>Queues a failure that never reached a server — DNS, TLS, a refused connection.</summary>
    public FakeHttpHandler TransportFailure(string message)
    {
        lock (_gate)
        {
            _responses.Enqueue(() => throw new HttpRequestException(message));
        }

        return this;
    }

    /// <summary>An <see cref="HttpClient"/> whose every call comes back from this handler.</summary>
    public HttpClient Client() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The body is read now, not lazily: HttpClient disposes the request content once the call
        // returns, so a test asserting on it afterwards would find it gone.
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

        var url = request.RequestUri!.ToString();
        Func<HttpResponseMessage> respond;

        lock (_gate)
        {
            _requests.Add(new Captured(request.Method, request.RequestUri!, headers, body));

            var route = _routes.Find(r => url.Contains(r.Fragment, StringComparison.Ordinal));
            respond = route.Respond
                ?? (_responses.Count > 0
                    ? _responses.Dequeue()
                    : throw new InvalidOperationException(
                        $"the client made an unexpected request with no canned response queued: {request.Method} {url}"));
        }

        // Outside the lock: a queued TransportFailure throws from here, and a test that also queued
        // routes should not have its handler left locked by the throw.
        return respond();
    }
}
