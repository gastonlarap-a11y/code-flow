using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace CodeFlow.ApiClient;

/// <summary>Which hash a digest challenge asked for.</summary>
internal enum DigestHash
{
    Md5,
    Sha256,
}

/// <summary>Everything one digest response is computed from.</summary>
internal readonly record struct DigestInput(
    DigestHash Hash,
    bool Session,
    string Username,
    string Password,
    string Realm,
    string Nonce,
    string Cnonce,
    string Nc,
    string? Qop,
    string Method,
    string Uri);

/// <summary>
/// HTTP Digest authentication (RFC 7616).
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-007</c>–<c>API-010</c>.
/// </summary>
/// <remarks>
/// Digest is a round trip, not a header: the request goes out unauthenticated, the server answers
/// <c>401</c> with a challenge, and the same request goes again carrying the response. That is why
/// it lives in the backend at all — the frontend cannot re-send a body it has already streamed.
/// </remarks>
internal static class DigestAuth
{
    /// <summary>Computes the <c>response</c> value of a digest authorization.</summary>
    /// <remarks>
    /// The <c>qop</c>-less branch is RFC 2069, which is still what some appliances speak, and it is
    /// a different formula rather than a degenerate case of the newer one.
    /// </remarks>
    public static string Response(DigestInput input)
    {
        var ha1 = Hash(input.Hash, $"{input.Username}:{input.Realm}:{input.Password}");

        if (input.Session)
        {
            ha1 = Hash(input.Hash, $"{ha1}:{input.Nonce}:{input.Cnonce}");
        }

        var ha2 = Hash(input.Hash, $"{input.Method}:{input.Uri}");

        return input.Qop is { } qop
            ? Hash(input.Hash, $"{ha1}:{input.Nonce}:{input.Nc}:{input.Cnonce}:{qop}:{ha2}")
            : Hash(input.Hash, $"{ha1}:{input.Nonce}:{ha2}");
    }

    /// <summary>
    /// Finds the Digest challenge among the <c>WWW-Authenticate</c> headers.
    /// </summary>
    /// <remarks>
    /// <b><c>BUG-API-b</c>, reproduced.</b> Each header <em>line</em> is tested for a leading
    /// <c>Digest</c>, so a server that combines several schemes into one value — the RFC permits
    /// <c>Basic realm="x", Digest realm="y", nonce="z"</c> — is missed unless Digest comes first,
    /// and the client falls back to sending nothing. Fixing it would mean splitting on scheme
    /// boundaries, which is a different parser; 1.7.2 does not, so neither does this.
    /// </remarks>
    public static Dictionary<string, string>? Challenge(IEnumerable<string> wwwAuthenticate)
    {
        foreach (var value in wwwAuthenticate)
        {
            var trimmed = value.TrimStart();

            if (trimmed.Length >= 6 && trimmed[..6].Equals("Digest", StringComparison.OrdinalIgnoreCase))
            {
                return ParseParameters(trimmed[6..].TrimStart());
            }
        }

        return null;
    }

    /// <summary>
    /// Parses <c>key=value</c> and <c>key="value"</c> pairs.
    /// </summary>
    /// <remarks>
    /// Not a <c>Split(',')</c>: a quoted value may itself contain commas, and
    /// <c>qop="auth,auth-int"</c> is the common case rather than an exotic one. Backslash escapes
    /// inside a quoted value are honoured.
    /// </remarks>
    public static Dictionary<string, string> ParseParameters(string input)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;

        while (true)
        {
            var key = new StringBuilder();
            while (index < input.Length && input[index] != '=' && input[index] != ',')
            {
                key.Append(input[index++]);
            }

            var name = key.ToString().Trim().ToLowerInvariant();
            var value = new StringBuilder();

            if (index < input.Length && input[index] == '=')
            {
                index++;

                if (index < input.Length && input[index] == '"')
                {
                    index++;
                    var escaped = false;

                    while (index < input.Length)
                    {
                        var c = input[index++];

                        if (escaped)
                        {
                            value.Append(c);
                            escaped = false;
                        }
                        else if (c == '\\')
                        {
                            escaped = true;
                        }
                        else if (c == '"')
                        {
                            break;
                        }
                        else
                        {
                            value.Append(c);
                        }
                    }
                }
                else
                {
                    while (index < input.Length && input[index] != ',')
                    {
                        value.Append(input[index++]);
                    }
                }
            }

            if (name.Length > 0)
            {
                parameters[name] = value.ToString().Trim();
            }

            // Skip the separator and any padding before the next key.
            while (index < input.Length && (input[index] == ',' || char.IsWhiteSpace(input[index])))
            {
                index++;
            }

            if (index >= input.Length)
            {
                return parameters;
            }
        }
    }

    /// <summary>Builds the <c>Authorization</c> header for one challenge.</summary>
    /// <remarks>
    /// A fresh <c>cnonce</c> and <c>nc=00000001</c> every time: this client never reuses a nonce
    /// across sends, so the counter has nothing to count.
    /// </remarks>
    public static string Authorization(
        string username, string password, string method, Uri url, IReadOnlyDictionary<string, string> challenge)
    {
        var realm = challenge.GetValueOrDefault("realm", string.Empty);

        var nonce = challenge.GetValueOrDefault("nonce", string.Empty);
        if (nonce.Length == 0)
        {
            throw new InvalidOperationException(
                "The digest challenge has no nonce, so no response can be computed");
        }

        var algorithm = challenge.GetValueOrDefault("algorithm", "MD5");
        var upper = algorithm.ToUpperInvariant();
        var session = upper.EndsWith("-SESS", StringComparison.Ordinal);
        var named = TrimSession(upper);

        var hash = named switch
        {
            "MD5" => DigestHash.Md5,
            "SHA-256" => DigestHash.Sha256,
            _ => throw new InvalidOperationException(
                $"The server asked for digest algorithm '{named}', which is not supported " +
                "(MD5, MD5-sess, SHA-256 and SHA-256-sess are)"),
        };

        var offered = challenge.GetValueOrDefault("qop", string.Empty);
        string? qop;

        if (offered.Length == 0)
        {
            qop = null;
        }
        else if (offered.Split(',').Any(q => q.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase)))
        {
            qop = "auth";
        }
        else
        {
            throw new InvalidOperationException(
                $"The server only offers digest qop='{offered}'; this client implements qop=auth");
        }

        var cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        const string Nc = "00000001";
        var uri = url.Query.Length > 0 ? $"{url.AbsolutePath}{url.Query}" : url.AbsolutePath;

        var response = Response(new DigestInput(
            hash, session, username, password, realm, nonce, cnonce, Nc, qop, method, uri));

        var header = new StringBuilder(
            $"Digest username=\"{username}\", realm=\"{realm}\", nonce=\"{nonce}\", " +
            $"uri=\"{uri}\", response=\"{response}\"");

        if (challenge.ContainsKey("algorithm"))
        {
            header.Append($", algorithm={algorithm}");
        }

        if (challenge.TryGetValue("opaque", out var opaque))
        {
            header.Append($", opaque=\"{opaque}\"");
        }

        if (qop is not null)
        {
            header.Append($", qop={qop}, nc={Nc}, cnonce=\"{cnonce}\"");
        }

        return header.ToString();
    }

    private static string TrimSession(string algorithm) =>
        algorithm.EndsWith("-SESS", StringComparison.Ordinal) ? algorithm[..^5] : algorithm;

    /// <summary>Hashes one step of the digest, with the algorithm the server asked for.</summary>
    /// <remarks>
    /// MD5 is not a choice made here. RFC 7616 defines it as Digest's default algorithm, and a
    /// server that offers only <c>algorithm=MD5</c> — which most that speak Digest at all do —
    /// cannot be authenticated against with anything else. Refusing it would not make the exchange
    /// stronger; it would make the feature not work, and the user would go and use curl instead.
    /// SHA-256 is supported and preferred wherever the server offers it.
    /// </remarks>
    [SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification =
            "Digest's wire format mandates MD5 (RFC 7616 §6.1); it is the protocol's choice, not " +
            "this application's, and SHA-256 is used whenever a server offers it.")]
    private static string Hash(DigestHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        return Convert.ToHexStringLower(hash == DigestHash.Md5 ? MD5.HashData(bytes) : SHA256.HashData(bytes));
    }
}
