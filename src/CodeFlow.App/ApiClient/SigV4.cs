using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeFlow.ApiClient;

/// <summary>What one request contributes to its own signature.</summary>
internal readonly record struct SigV4Request(
    string Method,
    string CanonicalUri,
    string CanonicalQuery,
    IReadOnlyList<(string Name, string Value)> Headers,
    string PayloadHash);

/// <summary>The credentials and scope a signature is computed under.</summary>
internal readonly record struct SigV4Credentials(
    string AccessKey,
    string SecretKey,
    string SessionToken,
    string Region,
    string Service);

/// <summary>
/// AWS Signature Version 4. See <c>docs/business-rules/08-api-client.md</c>,
/// <c>API-011</c>–<c>API-015</c>.
/// </summary>
/// <remarks>
/// Written out rather than taken from the AWS SDK, exactly as 1.7.2 writes it out: the SDK
/// signs requests it built itself, and this has to sign one the user typed. Every step is checked
/// against AWS's own published <c>aws-sig-v4-test-suite</c> vectors.
/// </remarks>
internal static class SigV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// Headers the transport rewrites or adds after signing.
    /// </summary>
    /// <remarks>
    /// Signing them would be pointless at best and break the request at worst, which is why the AWS
    /// SDKs exclude the same list.
    /// </remarks>
    private static readonly HashSet<string> Unsignable = new(StringComparer.Ordinal)
    {
        "accept-encoding", "authorization", "connection", "content-length", "expect",
        "keep-alive", "proxy-authorization", "te", "transfer-encoding", "user-agent",
    };

    /// <summary>Whether a header is excluded from the signature.</summary>
    public static bool IsUnsignable(string name) => Unsignable.Contains(name.ToLowerInvariant());

    /// <summary>
    /// The headers a signed request gains: the date, the payload hash, the session token when there
    /// is one, and the <c>Authorization</c> that ties them together.
    /// </summary>
    /// <remarks>
    /// <c>host</c> is signed but not returned — the transport sets it itself, and returning it
    /// would have the request carry two.
    /// </remarks>
    public static IReadOnlyList<(string Name, string Value)> Headers(
        string method,
        Uri url,
        IReadOnlyList<(string Name, string Value)> headers,
        string payloadHash,
        SigV4Credentials credentials,
        string amzDate)
    {
        if (credentials.AccessKey.Length == 0 || credentials.SecretKey.Length == 0)
        {
            throw new InvalidOperationException("AWS SigV4 needs both an access key and a secret key");
        }

        if (credentials.Region.Length == 0 || credentials.Service.Length == 0)
        {
            throw new InvalidOperationException("AWS SigV4 needs both a region and a service name");
        }

        var authority = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";

        var extra = new List<(string Name, string Value)>
        {
            ("x-amz-date", amzDate),
            ("x-amz-content-sha256", payloadHash),
        };

        if (credentials.SessionToken.Length > 0)
        {
            extra.Add(("x-amz-security-token", credentials.SessionToken));
        }

        var toSign = new List<(string Name, string Value)> { ("host", authority) };
        toSign.AddRange(headers.Where(h => !IsUnsignable(h.Name)));
        toSign.AddRange(extra);

        var (signedHeaders, signature) = Sign(
            new SigV4Request(method, CanonicalUri(url, credentials.Service), CanonicalQuery(url), toSign, payloadHash),
            credentials,
            amzDate);

        var credential =
            $"{credentials.AccessKey}/{amzDate[..8]}/{credentials.Region}/{credentials.Service}/aws4_request";

        extra.Add((
            "authorization",
            $"{Algorithm} Credential={credential}, SignedHeaders={signedHeaders}, Signature={signature}"));

        return extra;
    }

    /// <summary>Signs a canonical request and answers its signed-header list and signature.</summary>
    public static (string SignedHeaders, string Signature) Sign(
        SigV4Request request, SigV4Credentials credentials, string amzDate)
    {
        var merged = new List<(string Name, string Value)>();

        foreach (var (name, value) in request.Headers)
        {
            var normalized = NormalizeValue(value);
            var index = merged.FindIndex(h => string.Equals(h.Name, name, StringComparison.Ordinal));

            if (index >= 0)
            {
                // Repeated headers are signed as one comma-joined value, in the order sent.
                merged[index] = (merged[index].Name, $"{merged[index].Value},{normalized}");
            }
            else
            {
                merged.Add((name, normalized));
            }
        }

        merged.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var canonicalHeaders = string.Concat(merged.Select(h => $"{h.Name}:{h.Value}\n"));
        var signedHeaders = string.Join(";", merged.Select(h => h.Name));

        var canonicalRequest =
            $"{request.Method}\n{request.CanonicalUri}\n{request.CanonicalQuery}\n" +
            $"{canonicalHeaders}\n{signedHeaders}\n{request.PayloadHash}";

        var date = amzDate[..8];
        var scope = $"{date}/{credentials.Region}/{credentials.Service}/aws4_request";
        var stringToSign = $"{Algorithm}\n{amzDate}\n{scope}\n{HexSha256(Encoding.UTF8.GetBytes(canonicalRequest))}";

        var key = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{credentials.SecretKey}"), Encoding.UTF8.GetBytes(date));
        key = HmacSha256(key, Encoding.UTF8.GetBytes(credentials.Region));
        key = HmacSha256(key, Encoding.UTF8.GetBytes(credentials.Service));
        key = HmacSha256(key, "aws4_request"u8.ToArray());

        return (signedHeaders, Convert.ToHexStringLower(HmacSha256(key, Encoding.UTF8.GetBytes(stringToSign))));
    }

    /// <summary>Collapses every run of whitespace to one space, as the canonical form requires.</summary>
    internal static string NormalizeValue(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The canonical path.
    /// </summary>
    /// <remarks>
    /// S3 is the exception: its keys are already the path, so encoding them again would sign a
    /// different object than the one requested. Everything else is percent-encoded once, with
    /// <c>/</c> left literal.
    /// </remarks>
    internal static string CanonicalUri(Uri url, string service)
    {
        var path = url.AbsolutePath.Length == 0 ? "/" : url.AbsolutePath;

        return service.Equals("s3", StringComparison.OrdinalIgnoreCase) ? path : Encode(path, keepSlash: true);
    }

    /// <summary>The canonical query: every pair re-encoded, then sorted by the encoded text.</summary>
    /// <remarks>
    /// Sorting after encoding rather than before is what the specification says and is observable —
    /// the two orders differ for keys that encode to different bytes than they read as.
    /// </remarks>
    internal static string CanonicalQuery(Uri url)
    {
        var query = url.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return string.Empty;
        }

        var pairs = new List<(string Key, string Value)>();

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            var key = split < 0 ? pair : pair[..split];
            var value = split < 0 ? string.Empty : pair[(split + 1)..];

            pairs.Add((Encode(Uri.UnescapeDataString(key), keepSlash: false),
                       Encode(Uri.UnescapeDataString(value), keepSlash: false)));
        }

        pairs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key) is var byKey && byKey != 0
            ? byKey
            : string.CompareOrdinal(a.Value, b.Value));

        return string.Join("&", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    public static string HexSha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] HmacSha256(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    /// <summary>
    /// Percent-encodes to SigV4's rules: only <c>A-Za-z0-9-_.~</c> survive, and the escapes are
    /// uppercase.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Uri.EscapeDataString(string)"/>: it leaves <c>!*'()</c> alone, which AWS
    /// requires encoded, and a single unencoded character produces a signature mismatch with no
    /// explanation from the service.
    /// </remarks>
    private static string Encode(string value, bool keepSlash)
    {
        var encoded = new StringBuilder(value.Length);

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' || (keepSlash && c == '/'))
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }
}
