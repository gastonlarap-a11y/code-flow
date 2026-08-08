using System.Text.Json.Serialization;

namespace CodeFlow.ApiClient;

/// <summary>A cookie the server set, as it reaches the jar.</summary>
public sealed record ParsedCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    string? Expires,
    bool Secure,
    bool HttpOnly);

/// <summary>Per-request transport settings.</summary>
/// <param name="KeepAuthOnRedirect">
/// Whether <c>Authorization</c> survives a redirect that changes host. Off by default, because
/// forwarding a bearer token to whatever host a 302 names is a credential leak.
/// </param>
/// <param name="ProxyUrl"><c>""</c> = direct connection.</param>
/// <param name="ClientCertPath">PKCS#12 (<c>.p12</c>/<c>.pfx</c>) or a PEM bundle, for mTLS.</param>
/// <param name="CaCertPath">Extra PEM CA bundle to trust on top of the system roots.</param>
/// <param name="Cookies">Cookies the caller already matched against this URL.</param>
/// <param name="MaxResponseBytes">
/// Hard cap on how much of a response body is buffered; <c>0</c> = unlimited. Reaching it truncates
/// rather than fails.
/// </param>
/// <remarks>
/// <b>The defaults live on the constructor, not on property initialisers.</b> System.Text.Json's
/// source generator does not run initialisers for members a payload omits — it runs constructor
/// defaults — and the renderer omits most of these. Written the other way, a request that left out
/// <c>options</c> arrived with every field null and failed with a NullReferenceException before it
/// reached the network.
/// </remarks>
public sealed record NetworkOptions(
    [property: JsonPropertyName("timeout_ms")] long TimeoutMs = 30_000,
    [property: JsonPropertyName("follow_redirects")] bool FollowRedirects = true,
    [property: JsonPropertyName("max_redirects")] int MaxRedirects = 10,
    [property: JsonPropertyName("verify_ssl")] bool VerifySsl = true,
    [property: JsonPropertyName("keep_auth_on_redirect")] bool KeepAuthOnRedirect = false,
    [property: JsonPropertyName("proxy_url")] string ProxyUrl = "",
    [property: JsonPropertyName("client_cert_path")] string ClientCertPath = "",
    [property: JsonPropertyName("client_cert_password")] string ClientCertPassword = "",
    [property: JsonPropertyName("ca_cert_path")] string CaCertPath = "",
    [property: JsonPropertyName("cookies")] IReadOnlyList<IReadOnlyList<string>>? Cookies = null,
    [property: JsonPropertyName("max_response_bytes")] long MaxResponseBytes = 50 * 1024 * 1024)
{
    /// <summary>The jar, never null — an omitted list means no cookies, not a crash.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Jar => Cookies ?? [];
}

/// <summary>One multipart field. A part is a file when <see cref="FilePath"/> is set.</summary>
public sealed record FormPart(
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("file_path")] string? FilePath = null,
    [property: JsonPropertyName("content_type")] string? ContentType = null);

/// <summary>Auth the frontend cannot perform on its own.</summary>
/// <remarks>
/// Digest needs a round trip the frontend cannot repeat once a body has been streamed, and SigV4
/// signs the final method, URL, headers and body — which only the transport knows.
/// </remarks>
/// <param name="Kind"><c>digest</c> or <c>awsv4</c>.</param>
public sealed record BackendAuth(
    [property: JsonPropertyName("kind")] string Kind = "",
    [property: JsonPropertyName("username")] string Username = "",
    [property: JsonPropertyName("password")] string Password = "",
    [property: JsonPropertyName("access_key")] string AccessKey = "",
    [property: JsonPropertyName("secret_key")] string SecretKey = "",
    [property: JsonPropertyName("session_token")] string SessionToken = "",
    [property: JsonPropertyName("region")] string Region = "",
    [property: JsonPropertyName("service")] string Service = "");

/// <summary>A fully-resolved request. At most one body representation is populated.</summary>
/// <param name="BodyBase64">Base64 — used for binary payloads assembled in the webview.</param>
/// <param name="BodyFile">
/// Absolute path streamed from disk, so a multi-GB upload never enters the heap.
/// </param>
public sealed record HttpSendRequest(
    [property: JsonPropertyName("method")] string Method = "GET",
    [property: JsonPropertyName("url")] string Url = "",
    [property: JsonPropertyName("headers")] IReadOnlyList<IReadOnlyList<string>>? Headers = null,
    [property: JsonPropertyName("body_text")] string? BodyText = null,
    [property: JsonPropertyName("body_base64")] string? BodyBase64 = null,
    [property: JsonPropertyName("body_file")] string? BodyFile = null,
    [property: JsonPropertyName("form_data")] IReadOnlyList<FormPart>? FormData = null,
    [property: JsonPropertyName("urlencoded")] IReadOnlyList<IReadOnlyList<string>>? Urlencoded = null,
    [property: JsonPropertyName("auth")] BackendAuth? Auth = null,
    [property: JsonPropertyName("options")] NetworkOptions? Options = null)
{
    /// <summary>The headers, never null.</summary>
    public IReadOnlyList<IReadOnlyList<string>> HeaderPairs => Headers ?? [];

    /// <summary>The transport settings, defaulted when the caller sent none.</summary>
    public NetworkOptions Transport => Options ?? new NetworkOptions();
}

/// <summary>How long each phase took.</summary>
/// <remarks>
/// DNS, connect and TLS are reported as <c>-1</c>, which is the contract's "unavailable" — the
/// reference reports the same, because neither transport hands back a connection trace on a
/// response. Splitting <c>first_byte_ms</c> into three invented numbers would be worse than
/// admitting there are none.
/// </remarks>
public sealed record ResponseTimings(
    long DnsMs,
    long ConnectMs,
    long TlsMs,
    long FirstByteMs,
    long DownloadMs,
    long TotalMs);

/// <summary>What actually went on the wire, including headers the transport added itself.</summary>
/// <remarks>This is what makes the request console honest.</remarks>
public sealed record SentRequestSummary(
    string Method,
    string Url,
    IReadOnlyList<IReadOnlyList<string>> Headers,
    string BodyPreview);

/// <summary>One completed HTTP exchange.</summary>
public sealed record HttpResponse(
    int Status,
    string StatusText,
    string HttpVersion,
    IReadOnlyList<IReadOnlyList<string>> Headers,
    string BodyText,
    string? BodyBase64,
    long SizeBytes,
    long DurationMs,
    ResponseTimings Timings,
    IReadOnlyList<string> Redirects,
    IReadOnlyList<ParsedCookie> SetCookies,
    SentRequestSummary Sent);
