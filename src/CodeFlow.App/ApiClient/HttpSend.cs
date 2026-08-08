using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CodeFlow.ApiClient;

/// <summary>
/// The HTTP and GraphQL transport.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-001</c>–<c>API-024</c>.
/// </summary>
/// <remarks>
/// <para>
/// GraphQL is not a separate path: it is a POST with a JSON body, and 1.7.2 has no
/// GraphQL-specific code either.
/// </para>
/// <para>
/// Redirects are followed by hand rather than by the handler. The transport has to record every
/// hop for the console, and <c>keepAuthOnRedirect</c> has to be able to keep an <c>Authorization</c>
/// header across a host change that .NET would otherwise strip with no way to opt out — the same
/// two reasons 1.7.2 writes its own policy.
/// </para>
/// </remarks>
internal static class HttpSend
{
    /// <summary>How much of the body the request console shows.</summary>
    private const int BodyPreviewLimit = 2048;

    /// <summary>`VERBATIM`, and mirrored in <c>renderer/src/lib/api/send.ts</c>.</summary>
    private const string AdvertisedEncodings = "gzip, br, deflate";

    /// <summary>Sends one request and reads its whole response.</summary>
    public static async Task<HttpResponse> SendAsync(HttpSendRequest request, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();

        var method = ParseMethod(request.Method);
        var startUrl = ParseUrl(request.Url);

        using var handler = BuildHandler(request.Transport);
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = request.Transport.TimeoutMs > 0
                ? TimeSpan.FromMilliseconds(request.Transport.TimeoutMs)
                : Timeout.InfiniteTimeSpan,
        };

        var hops = new List<string>();
        var exchange = await ExchangeAsync(client, request, method, startUrl, [], hops, cancellationToken)
            .ConfigureAwait(false);

        // Digest is a challenge/response scheme: the first send exists only to collect the nonce,
        // and the body has to go out again with the second.
        if (request.Auth is { Kind: "digest" } digest
            && exchange.Response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var challenge = DigestAuth.Challenge(WwwAuthenticate(exchange.Response))
                ?? throw new InvalidOperationException(
                    $"{method} {startUrl} returned 401 but no 'WWW-Authenticate: Digest' challenge, " +
                    "so the digest handshake cannot continue");

            // Re-challenged against wherever the first attempt landed: the nonce and the signed
            // request-target belong to that URL, not to the one originally typed.
            var target = exchange.FinalUrl;
            var header = DigestAuth.Authorization(
                digest.Username, digest.Password, method.Method, target, challenge);

            exchange.Response.Dispose();
            exchange = await ExchangeAsync(
                client, request, method, target, [("authorization", header)], hops, cancellationToken)
                .ConfigureAwait(false);
        }

        using var response = exchange.Response;

        var headers = HeaderPairs(response);
        var contentType = ContentTypeOf(headers);
        var setCookies = SetCookies(response, exchange.FinalUrl);

        var download = Stopwatch.StartNew();
        var body = await ReadBodyAsync(response, request.Transport.MaxResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        download.Stop();

        var (text, base64) = ResponseDecoding.DecodeBody(body, contentType);

        // The contract is "every hop, final URL last".
        var redirects = new List<string>(hops);
        if (redirects.Count > 0 && redirects[^1] != exchange.FinalUrl.AbsoluteUri)
        {
            redirects.Add(exchange.FinalUrl.AbsoluteUri);
        }

        total.Stop();

        return new HttpResponse(
            (int)response.StatusCode,
            ReasonPhrase(response),
            $"HTTP/{response.Version}",
            headers,
            text,
            base64,
            body.Length,
            (long)total.Elapsed.TotalMilliseconds,
            // -1 is the contract's "unavailable", not zero. .NET reports no connection trace on a
            // response, so splitting first_byte_ms into DNS, connect and TLS would be inventing
            // three numbers — which is worse than admitting there are none.
            new ResponseTimings(-1, -1, -1, exchange.FirstByteMs, (long)download.Elapsed.TotalMilliseconds,
                (long)total.Elapsed.TotalMilliseconds),
            redirects,
            setCookies,
            exchange.Sent);
    }

    /// <summary>One request, its redirects followed by hand.</summary>
    private static async Task<Exchange> ExchangeAsync(
        HttpClient client,
        HttpSendRequest request,
        HttpMethod method,
        Uri url,
        IReadOnlyList<(string Name, string Value)> extraHeaders,
        List<string> hops,
        CancellationToken cancellationToken)
    {
        SentRequestSummary? sent = null;
        var withBody = true;

        while (true)
        {
            var built = await BuildAsync(request, method, url, extraHeaders, withBody).ConfigureAwait(false);

            // Disposed per iteration, not per call. `HttpClient` does not take ownership of the
            // request, and `BuildAsync` opens a `FileStream` for a file body or a multipart file
            // part — a fresh one on every pass, because each redirect hop rebuilds from scratch
            // and the digest handshake re-enters this method for its second send. Leaving them to
            // the finalizer keeps the uploaded file locked on Windows for an indeterminate time
            // after the request has already finished.
            using var message = built.Request;

            // The summary is the *first* request of the exchange: what the user actually asked to
            // send, not the last hop it happened to end on.
            sent ??= built.Summary;

            var firstByte = Stopwatch.StartNew();
            HttpResponseMessage response;

            try
            {
                response = await client
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException($"{method} {url} failed: {RootCause(e)}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"{method} {url} timed out after {request.Transport.TimeoutMs} ms");
            }

            firstByte.Stop();

            var next = RedirectTarget(request.Transport, response, url);
            if (next is null)
            {
                return new Exchange(response, sent, (long)firstByte.Elapsed.TotalMilliseconds, url);
            }

            if (hops.Count >= request.Transport.MaxRedirects)
            {
                response.Dispose();
                throw new InvalidOperationException(
                    $"{method} {url} went through more than {request.Transport.MaxRedirects} redirects");
            }

            hops.Add(next.AbsoluteUri);

            // Browsers — and every other client in practice — turn a redirected POST into a bodiless
            // GET for 301, 302 and 303; only 307 and 308 promise the method and body survive.
            //
            // BUG-API-a, reproduced: the 301/302 downgrade applies to *any* method that is not GET
            // or HEAD, not only to POST as 1.7.2's own adjacent comment claims. A redirected
            // DELETE or PUT therefore arrives as a GET.
            var status = (int)response.StatusCode;
            if (status == 303 || (status is 301 or 302 && method != HttpMethod.Get && method != HttpMethod.Head))
            {
                method = HttpMethod.Get;
                withBody = false;
            }

            response.Dispose();
            url = next;
        }
    }

    /// <summary>Where a redirect points, or null when this response is the end of the line.</summary>
    private static Uri? RedirectTarget(NetworkOptions options, HttpResponseMessage response, Uri current)
    {
        if (!options.FollowRedirects || (int)response.StatusCode is < 300 or > 399)
        {
            return null;
        }

        if (response.Headers.Location is not { } location)
        {
            return null;
        }

        var target = location.IsAbsoluteUri ? location : new Uri(current, location);

        // Off by default, because forwarding a bearer token to whatever host a 302 names is a
        // credential leak. When it is off the hop still happens — the Authorization header is
        // simply not carried across it.
        return target;
    }

    /// <summary>Assembles one request from scratch.</summary>
    /// <remarks>
    /// Everything that has to happen per attempt — re-opening a streamed file, re-signing SigV4
    /// against the new target — lives here, which is why a digest re-send and a redirect hop call
    /// it again rather than cloning a request.
    /// </remarks>
    private static async Task<Built> BuildAsync(
        HttpSendRequest request,
        HttpMethod method,
        Uri url,
        IReadOnlyList<(string Name, string Value)> extraHeaders,
        bool withBody)
    {
        var headers = request.HeaderPairs
            .Where(pair => pair.Count >= 2)
            .Select(pair => (Name: pair[0].Trim().ToLowerInvariant(), Value: pair[1]))
            .ToList();

        bool Has(string name) => headers.Any(h => h.Name == name);

        if (request.Transport.Jar.Count > 0 && !Has("cookie"))
        {
            headers.Add(("cookie",
                string.Join("; ", request.Transport.Jar.Where(c => c.Count >= 2).Select(c => $"{c[0]}={c[1]}"))));
        }

        var body = withBody ? await PreparedBody.ForAsync(request).ConfigureAwait(false) : PreparedBody.None;

        if (body.ContentType is { } bodyContentType && !Has("content-type"))
        {
            headers.Add(("content-type", bodyContentType));
        }

        if (request.Auth is { Kind: "awsv4" } aws)
        {
            headers.AddRange(SigV4.Headers(
                method.Method,
                url,
                headers,
                body.PayloadHash,
                new SigV4Credentials(aws.AccessKey, aws.SecretKey, aws.SessionToken, aws.Region, aws.Service),
                DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)));
        }

        foreach (var (name, value) in extraHeaders)
        {
            headers.RemoveAll(h => h.Name == name);
            headers.Add((name, value));
        }

        // The multipart boundary is generated inside the content, so a caller-supplied
        // multipart content-type would describe a body it cannot delimit.
        if (body.IsMultipart)
        {
            headers.RemoveAll(h => h.Name == "content-type");
        }

        // Added to the list rather than straight onto the message, so the request console reports
        // it too: the summary's whole job is to show what actually went out, including what the
        // transport put there on the caller's behalf.
        if (!Has("accept-encoding"))
        {
            headers.Add(("accept-encoding", AdvertisedEncodings));
        }

        var message = new HttpRequestMessage(method, url) { Content = body.Content };

        foreach (var (name, value) in headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return new Built(message, new SentRequestSummary(
            method.Method,
            url.AbsoluteUri,
            [.. headers.Select(h => (IReadOnlyList<string>)[h.Name, h.Value])],
            body.Preview));
    }

    /// <summary>Reads the body, stopping at the cap rather than failing on it.</summary>
    /// <remarks>
    /// A truncated body is still worth showing — the alternative is an error where the user wanted
    /// the first megabyte of a large download.
    /// </remarks>
    private static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response, long cap, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[64 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (cap > 0 && buffer.Length + read >= cap)
            {
                buffer.Write(chunk, 0, (int)(cap - buffer.Length));
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static HttpMethod ParseMethod(string method)
    {
        var trimmed = method.Trim();

        return trimmed.Length > 0 && trimmed.All(c => char.IsAsciiLetter(c) || c is '-' or '_')
            ? new HttpMethod(trimmed.ToUpperInvariant())
            : throw new InvalidOperationException($"'{method}' is not a valid HTTP method");
    }

    private static Uri ParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException($"'{url}' is not a valid URL");
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"{parsed} uses the '{parsed.Scheme}' scheme; only http and https can be sent here");
        }

        return parsed;
    }

    /// <summary>One handler per send.</summary>
    /// <remarks>
    /// <c>API-001</c>: TLS verification, client identity, the CA bundle and the proxy are all
    /// handler-level settings, and this request's may differ from the last one's — so sharing one
    /// handler would silently apply the wrong settings rather than fail.
    /// </remarks>
    [SuppressMessage(
        "Security",
        "CA5359:Do Not Disable Certificate Validation",
        Justification =
            "`verify_ssl: false` is a per-request toggle the user sets in the API tester, the same " +
            "affordance as curl's -k, and testing against a staging host with a self-signed " +
            "certificate is what an API client is for. It is off by default and applies to one " +
            "request; nothing else in the application disables validation.")]
    private static SocketsHttpHandler BuildHandler(NetworkOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            // Followed by hand: see the note on this class.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };

        if (options.ProxyUrl.Length > 0)
        {
            handler.Proxy = new WebProxy(options.ProxyUrl);
            handler.UseProxy = true;
        }

        if (!options.VerifySsl)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        if (options.ClientCertPath.Length > 0)
        {
            handler.SslOptions.ClientCertificates =
            [
                X509CertificateLoader.LoadPkcs12FromFile(options.ClientCertPath, options.ClientCertPassword),
            ];
        }

        return handler;
    }

    private static IEnumerable<string> WwwAuthenticate(HttpResponseMessage response) =>
        response.Headers.TryGetValues("WWW-Authenticate", out var values) ? values : [];

    private static List<IReadOnlyList<string>> HeaderPairs(HttpResponseMessage response) =>
    [
        .. response.Headers.Concat(response.Content.Headers)
            .SelectMany(h => h.Value.Select(v => (IReadOnlyList<string>)[h.Key.ToLowerInvariant(), v])),
    ];

    private static string? ContentTypeOf(IEnumerable<IReadOnlyList<string>> headers) =>
        headers.FirstOrDefault(h => h[0].Equals("content-type", StringComparison.OrdinalIgnoreCase))?[1]
            .ToLowerInvariant();

    private static List<ParsedCookie> SetCookies(HttpResponseMessage response, Uri url) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Select(v => ResponseDecoding.ParseSetCookie(v, url, DateTimeOffset.UtcNow))
                .Where(c => c is not null)
                .Select(c => c!)]
            : [];

    private static string ReasonPhrase(HttpResponseMessage response) => response.ReasonPhrase ?? string.Empty;

    /// <summary>The innermost message, which is the one that names the real failure.</summary>
    private static string RootCause(Exception error)
    {
        var current = error;
        while (current.InnerException is { } inner)
        {
            current = inner;
        }

        return current.Message;
    }

    private sealed record Built(HttpRequestMessage Request, SentRequestSummary Summary);

    private sealed record Exchange(
        HttpResponseMessage Response, SentRequestSummary Sent, long FirstByteMs, Uri FinalUrl);

    /// <summary>The body, in whichever of its five shapes the caller populated.</summary>
    private sealed class PreparedBody
    {
        public static readonly PreparedBody None = new();

        public HttpContent? Content { get; private init; }

        public string? ContentType { get; private init; }

        public string Preview { get; private init; } = string.Empty;

        public bool IsMultipart { get; private init; }

        /// <summary>SHA-256 of the payload, which SigV4 signs.</summary>
        public string PayloadHash { get; private init; } = SigV4.HexSha256([]);

        public static async Task<PreparedBody> ForAsync(HttpSendRequest request)
        {
            if (request.FormData is { Count: > 0 } parts)
            {
                var form = new MultipartFormDataContent();

                foreach (var part in parts)
                {
                    if (part.FilePath is { Length: > 0 } path)
                    {
                        // Streamed rather than read: a multi-GB upload must never enter the heap.
                        var file = new StreamContent(File.OpenRead(path), 64 * 1024);
                        if (part.ContentType is { Length: > 0 } type)
                        {
                            file.Headers.ContentType = new MediaTypeHeaderValue(type);
                        }

                        form.Add(file, part.Name, Path.GetFileName(path));
                    }
                    else
                    {
                        form.Add(new StringContent(part.Value ?? string.Empty), part.Name);
                    }
                }

                return new PreparedBody
                {
                    Content = form,
                    IsMultipart = true,
                    Preview = $"[multipart: {parts.Count} part(s)]",
                    // A streamed multipart body cannot be hashed without buffering it, and the
                    // reference does not buffer it either — the unsigned-payload sentinel is what
                    // SigV4 defines for exactly this case.
                    PayloadHash = "UNSIGNED-PAYLOAD",
                };
            }

            if (request.Urlencoded is { Count: > 0 } pairs)
            {
                var encoded = string.Join("&", pairs
                    .Where(p => p.Count >= 2)
                    .Select(p => $"{Uri.EscapeDataString(p[0])}={Uri.EscapeDataString(p[1])}"));

                return FromBytes(Encoding.UTF8.GetBytes(encoded), "application/x-www-form-urlencoded", encoded);
            }

            if (request.BodyFile is { Length: > 0 } bodyFile)
            {
                var info = new FileInfo(bodyFile);
                var content = new StreamContent(File.OpenRead(bodyFile), 64 * 1024);
                content.Headers.ContentLength = info.Length;

                return new PreparedBody
                {
                    Content = content,
                    Preview = $"[file: {bodyFile} ({info.Length} bytes)]",
                    PayloadHash = "UNSIGNED-PAYLOAD",
                };
            }

            if (request.BodyBase64 is { Length: > 0 } base64)
            {
                var bytes = Convert.FromBase64String(base64);

                return FromBytes(bytes, null, $"[binary: {bytes.Length} bytes]");
            }

            if (request.BodyText is { Length: > 0 } text)
            {
                return FromBytes(Encoding.UTF8.GetBytes(text), null, text);
            }

            await Task.CompletedTask.ConfigureAwait(false);

            return None;
        }

        private static PreparedBody FromBytes(byte[] bytes, string? contentType, string preview) => new()
        {
            Content = new ByteArrayContent(bytes),
            ContentType = contentType,
            Preview = Truncate(preview),
            PayloadHash = SigV4.HexSha256(bytes),
        };

        private static string Truncate(string preview) =>
            preview.Length <= BodyPreviewLimit ? preview : $"{preview[..BodyPreviewLimit]}…";
    }
}
