using System.Globalization;
using System.Text;

namespace CodeFlow.ApiClient;

/// <summary>
/// Turning a response body and a <c>Set-Cookie</c> header into something the UI can show.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-021</c>–<c>API-023</c>.
/// </summary>
internal static class ResponseDecoding
{
    /// <summary>
    /// Whether a media type carries text a human would want to read.
    /// </summary>
    /// <remarks>
    /// Structured types are matched by suffix (<c>+json</c>, <c>+xml</c>) rather than by an
    /// exhaustive list, because every vendor type in the wild —
    /// <c>application/vnd.github+json</c>, <c>application/problem+json</c> — is one, and a list
    /// would be wrong the day after it was written. `VERBATIM`: the explicit set below is copied
    /// entry for entry.
    /// </remarks>
    public static bool IsTextual(string contentType)
    {
        var media = contentType.Split(';')[0].Trim();

        return media.StartsWith("text/", StringComparison.Ordinal)
            || media.EndsWith("+json", StringComparison.Ordinal)
            || media.EndsWith("+xml", StringComparison.Ordinal)
            || media is "application/json"
                or "application/xml"
                or "application/javascript"
                or "application/x-javascript"
                or "application/ecmascript"
                or "application/graphql"
                or "application/x-www-form-urlencoded"
                or "application/x-ndjson"
                or "application/ld+json"
                or "application/sql"
                or "image/svg+xml";
    }

    /// <summary>The <c>charset</c> parameter of a content type, lowercased.</summary>
    public static string? Charset(string contentType)
    {
        foreach (var parameter in contentType.Split(';').Skip(1))
        {
            var split = parameter.IndexOf('=', StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }

            if (parameter[..split].Trim() == "charset")
            {
                return parameter[(split + 1)..].Trim().Trim('"').ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// A body with no declared type: text unless it looks like it is not.
    /// </summary>
    /// <remarks>
    /// A NUL byte is the signal — it cannot appear in any text encoding a server would send, and it
    /// appears almost immediately in every binary format worth naming. Only the head is examined so
    /// a large download is not scanned twice.
    /// </remarks>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes) =>
        bytes[..Math.Min(4096, bytes.Length)].Contains((byte)0);

    /// <summary>
    /// Turns the raw body into text or base64, exactly one of which is meaningful.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Validity is not the test — the declared type is.</b> A page can be served as
    /// <c>text/html; charset=UTF-8</c> and still contain bytes that are not valid UTF-8; refusing
    /// to show 80 KB of readable HTML because of them is worse in every way than showing it with a
    /// replacement character where the bad byte was, which is what every other client does. That is
    /// why this decodes leniently and <see cref="FileOps.ReadFileText"/> — a different job — does
    /// not.
    /// </para>
    /// <para>
    /// Latin-1 and Windows-1252 are transcoded rather than replaced, since a server that declares
    /// them means it, and they are the only legacy charsets common enough to be worth the code.
    /// </para>
    /// </remarks>
    public static (string Text, string? Base64) DecodeBody(byte[] bytes, string? contentType)
    {
        var textual = contentType is not null ? IsTextual(contentType) : !LooksBinary(bytes);

        if (!textual)
        {
            return (string.Empty, Convert.ToBase64String(bytes));
        }

        return (contentType is not null ? Charset(contentType) : null) switch
        {
            // Byte-for-code-point, which is what Latin-1 is: 1.7.2 maps each byte straight
            // to the char of the same value, and Windows-1252 is close enough that it does the same.
            "iso-8859-1" or "latin1" or "latin-1" or "windows-1252" or "cp1252" =>
                (string.Concat(bytes.Select(b => (char)b)), null),
            _ => (Encoding.UTF8.GetString(bytes), null),
        };
    }

    /// <summary>
    /// Parses one <c>Set-Cookie</c> value against the URL it arrived from.
    /// </summary>
    /// <remarks>
    /// <b><c>BUG-API-c</c>, reproduced.</b> The default path is the literal <c>"/"</c> rather than
    /// RFC 6265 §5.1.4's default-path algorithm, which would derive it from the request path — so a
    /// cookie set at <c>/v1/login</c> with no <c>Path</c> is stored as if it were site-wide and is
    /// then sent to paths the server never scoped it to. Correcting it would change which cookies
    /// this client sends.
    /// </remarks>
    public static ParsedCookie? ParseSetCookie(string value, Uri url, DateTimeOffset now)
    {
        var segments = value.Split(';');

        var first = segments[0];
        var split = first.IndexOf('=', StringComparison.Ordinal);
        if (split < 0)
        {
            return null;
        }

        var name = first[..split].Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var domain = url.Host;
        var path = "/";
        var secure = false;
        var httpOnly = false;
        long? maxAge = null;
        string? expires = null;

        foreach (var raw in segments.Skip(1))
        {
            var segment = raw.Trim();
            var equals = segment.IndexOf('=', StringComparison.Ordinal);
            var key = (equals < 0 ? segment : segment[..equals]).Trim();
            var attribute = equals < 0 ? string.Empty : segment[(equals + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                // A leading dot is the pre-RFC 6265 spelling of "and every subdomain"; the modern
                // semantics are identical without it, and the jar matches on the bare host.
                case "domain" when attribute.Length > 0:
                    domain = attribute.TrimStart('.');
                    break;
                case "path" when attribute.Length > 0:
                    path = attribute;
                    break;
                case "expires":
                    expires = attribute;
                    break;
                case "max-age":
                    maxAge = long.TryParse(attribute, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
                    break;
                case "secure":
                    secure = true;
                    break;
                case "httponly":
                    httpOnly = true;
                    break;
                default:
                    break;
            }
        }

        // RFC 6265: Max-Age wins over Expires wherever both are present.
        var resolved = maxAge is { } age
            ? now.AddSeconds(age).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
            : expires is not null ? ParseHttpDate(expires) : null;

        return new ParsedCookie(name, first[(split + 1)..].Trim(), domain, path, resolved, secure, httpOnly);
    }

    /// <summary>
    /// The three date formats RFC 6265 requires a client to tolerate.
    /// </summary>
    /// <remarks>
    /// Gives up rather than guessing: an unparseable expiry is reported as a session cookie, which
    /// is the conservative reading — the cookie is dropped when the app closes instead of being
    /// kept for a date nobody could read.
    /// </remarks>
    internal static string? ParseHttpDate(string raw)
    {
        string[] formats =
        [
            "ddd, dd MMM yyyy HH:mm:ss 'GMT'",   // RFC 1123, what everything sends
            "dddd, dd-MMM-yy HH:mm:ss 'GMT'",    // RFC 850, still emitted by old appliances
            "ddd MMM d HH:mm:ss yyyy",           // asctime
        ];

        return DateTimeOffset.TryParseExact(
            raw.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
            : null;
    }
}
