using System.Text;
using System.Text.Json;
using CodeFlow.ApiClient;
using CodeFlow.Tests.TestVectors;
using Xunit;

namespace CodeFlow.Tests.ApiClient;

/// <summary>
/// Digest authentication, <c>Set-Cookie</c> parsing and response decoding, against the vectors
///.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-007</c>–<c>API-023</c>.
/// </summary>
public sealed class HttpDecodingTests
{
    private const string Vectors = "http.vectors.json";

    // ---------- digest ----------

    /// <summary>RFC 2617's own worked example, and the RFC 2069 form some appliances still speak.</summary>
    [Theory]
    [InlineData("digest-rfc2617-worked-example")]
    [InlineData("digest-rfc2069-fallback")]
    public void A_published_digest_vector_is_reproduced(string caseId)
    {
        var testCase = Vector(caseId);
        var input = testCase.Input;

        var qop = input.GetProperty("qop");

        var response = DigestAuth.Response(new DigestInput(
            input.GetProperty("hash").GetString() == "Md5" ? DigestHash.Md5 : DigestHash.Sha256,
            input.GetProperty("session").GetBoolean(),
            input.GetProperty("username").GetString()!,
            input.GetProperty("password").GetString()!,
            input.GetProperty("realm").GetString()!,
            input.GetProperty("nonce").GetString()!,
            input.GetProperty("cnonce").GetString()!,
            input.GetProperty("nc").GetString()!,
            qop.ValueKind == JsonValueKind.Null ? null : qop.GetString(),
            input.GetProperty("method").GetString()!,
            input.GetProperty("uri").GetString()!));

        Assert.Equal(testCase.Expected.GetProperty("response").GetString(), response);
    }

    /// <summary>A quoted value may hold commas, which is why this is not a split.</summary>
    [Fact]
    public void Auth_parameters_survive_a_quoted_value_containing_commas()
    {
        var testCase = Vector("auth-params-quoted-commas");

        var parsed = DigestAuth.ParseParameters(testCase.Input.GetProperty("raw").GetString()!);

        foreach (var expected in testCase.Expected.EnumerateObject())
        {
            Assert.Equal(expected.Value.GetString(), parsed[expected.Name]);
        }
    }

    /// <summary>
    /// <c>BUG-API-b</c>, reproduced: the challenge is missed when Digest is not the first scheme in
    /// a combined header value.
    /// </summary>
    /// <remarks>
    /// The RFC permits a server to offer several schemes in one <c>WWW-Authenticate</c>. The
    /// reference tests each header <em>line</em> for a leading <c>Digest</c>, so that server gets
    /// no digest response at all. Correcting it means a different parser, so the defect is stated
    /// instead.
    /// </remarks>
    [Fact]
    public void A_digest_challenge_hiding_behind_another_scheme_is_not_seen()
    {
        Assert.NotNull(DigestAuth.Challenge(["Digest realm=\"x\", nonce=\"abc\""]));

        // Same information, one header, Basic first — and now nothing is found.
        Assert.Null(DigestAuth.Challenge(["Basic realm=\"x\", Digest realm=\"y\", nonce=\"abc\""]));
    }

    [Fact]
    public void A_challenge_on_its_own_line_is_found_whatever_the_case()
    {
        var challenge = DigestAuth.Challenge(["Basic realm=\"x\"", "digest realm=\"y\", nonce=\"abc\""]);

        Assert.Equal("y", Assert.IsType<Dictionary<string, string>>(challenge)["realm"]);
    }

    [Fact]
    public void A_challenge_with_no_nonce_cannot_be_answered()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => DigestAuth.Authorization(
            "user", "pass", "GET", new Uri("https://example.test/x"), new Dictionary<string, string>()));

        Assert.Equal("The digest challenge has no nonce, so no response can be computed", failure.Message);
    }

    [Fact]
    public void An_algorithm_this_client_cannot_compute_says_which_ones_it_can()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => DigestAuth.Authorization(
            "user", "pass", "GET", new Uri("https://example.test/x"),
            new Dictionary<string, string> { ["nonce"] = "abc", ["algorithm"] = "SHA-512" }));

        Assert.Equal(
            "The server asked for digest algorithm 'SHA-512', which is not supported " +
            "(MD5, MD5-sess, SHA-256 and SHA-256-sess are)",
            failure.Message);
    }

    [Fact]
    public void A_qop_this_client_does_not_implement_is_refused_rather_than_ignored()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => DigestAuth.Authorization(
            "user", "pass", "GET", new Uri("https://example.test/x"),
            new Dictionary<string, string> { ["nonce"] = "abc", ["qop"] = "auth-int" }));

        Assert.Equal(
            "The server only offers digest qop='auth-int'; this client implements qop=auth",
            failure.Message);
    }

    [Fact]
    public void The_authorization_header_carries_the_query_string_in_its_uri()
    {
        var header = DigestAuth.Authorization(
            "user", "pass", "GET", new Uri("https://example.test/dir/index.html?a=1"),
            new Dictionary<string, string> { ["nonce"] = "abc", ["realm"] = "r", ["qop"] = "auth" });

        Assert.Contains("uri=\"/dir/index.html?a=1\"", header, StringComparison.Ordinal);
        Assert.Contains("qop=auth, nc=00000001, cnonce=\"", header, StringComparison.Ordinal);
    }

    // ---------- cookies ----------

    [Fact]
    public void A_cookie_with_no_attributes_takes_the_host_and_the_root_path()
    {
        var testCase = Vector("cookie-defaults-host-and-root-path");

        var cookie = ResponseDecoding.ParseSetCookie(
            testCase.Input.GetProperty("raw_set_cookie").GetString()!,
            new Uri(testCase.Input.GetProperty("url").GetString()!),
            DateTimeOffset.UnixEpoch)!;

        var expected = testCase.Expected;
        Assert.Equal(expected.GetProperty("name").GetString(), cookie.Name);
        Assert.Equal(expected.GetProperty("value").GetString(), cookie.Value);
        Assert.Equal(expected.GetProperty("domain").GetString(), cookie.Domain);
        Assert.Equal(expected.GetProperty("secure").GetBoolean(), cookie.Secure);
        Assert.Equal(expected.GetProperty("http_only").GetBoolean(), cookie.HttpOnly);
        Assert.Null(cookie.Expires);

        // BUG-API-c: the default path is the literal "/", not RFC 6265's default-path algorithm,
        // which would have derived /v1 from the request. So this cookie is stored as site-wide and
        // will be sent to paths the server never scoped it to.
        Assert.Equal("/", cookie.Path);
    }

    [Fact]
    public void Domain_path_and_expiry_attributes_are_honoured()
    {
        var testCase = Vector("cookie-domain-path-expiry");

        var cookie = ResponseDecoding.ParseSetCookie(
            testCase.Input.GetProperty("raw_set_cookie").GetString()!,
            new Uri(testCase.Input.GetProperty("url").GetString()!),
            DateTimeOffset.UnixEpoch)!;

        var expected = testCase.Expected;

        // The leading dot is the pre-RFC6265 spelling of "and every subdomain"; the jar matches on
        // the bare host, so it is dropped.
        Assert.Equal(expected.GetProperty("domain").GetString(), cookie.Domain);
        Assert.Equal(expected.GetProperty("path").GetString(), cookie.Path);
        Assert.Equal(expected.GetProperty("expires").GetString(), cookie.Expires);
    }

    /// <summary>RFC 6265: <c>Max-Age</c> wins wherever both are present.</summary>
    [Fact]
    public void Max_age_beats_expires()
    {
        var cookie = ResponseDecoding.ParseSetCookie(
            "sid=abc; Expires=Wed, 21 Oct 2015 07:28:00 GMT; Max-Age=60",
            new Uri("https://example.test/"),
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal("1970-01-01T00:01:00+00:00", cookie.Expires);
    }

    /// <summary>An unparseable expiry is a session cookie, not a guess.</summary>
    [Fact]
    public void An_expiry_nobody_can_read_is_dropped_rather_than_invented()
    {
        var cookie = ResponseDecoding.ParseSetCookie(
            "sid=abc; Expires=whenever", new Uri("https://example.test/"), DateTimeOffset.UnixEpoch)!;

        Assert.Null(cookie.Expires);
    }

    [Theory]
    [InlineData("no-equals-sign")]
    [InlineData("=novalue")]
    [InlineData("   =x")]
    public void A_header_that_is_not_a_cookie_yields_nothing(string raw) =>
        Assert.Null(ResponseDecoding.ParseSetCookie(raw, new Uri("https://example.test/"), DateTimeOffset.UnixEpoch));

    // ---------- decoding ----------

    /// <summary>
    /// The declared type decides, not validity.
    /// </summary>
    /// <remarks>
    /// A page served as <c>text/html; charset=utf-8</c> can still hold a byte that is not valid
    /// UTF-8 — google.com is one. Refusing to show it is worse in every way than a replacement
    /// character where the bad byte was.
    /// </remarks>
    [Fact]
    public void A_declared_text_body_with_one_bad_byte_is_still_shown()
    {
        var testCase = Vector("decode-invalid-byte-still-text");
        var input = testCase.Input;

        var bytes = Encoding.UTF8.GetBytes(input.GetProperty("bytes_utf8_prefix").GetString()!)
            .Concat(Convert.FromHexString(input.GetProperty("extra_trailing_byte_hex").GetString()!))
            .ToArray();

        var (text, base64) = ResponseDecoding.DecodeBody(bytes, input.GetProperty("content_type").GetString());

        Assert.StartsWith(testCase.Expected.GetProperty("body_text_starts_with").GetString()!, text, StringComparison.Ordinal);
        Assert.EndsWith(testCase.Expected.GetProperty("body_text_ends_with").GetString()!, text, StringComparison.Ordinal);
        Assert.Null(base64);
    }

    [Fact]
    public void A_binary_body_becomes_base64_whether_or_not_it_declared_itself()
    {
        var testCase = Vector("decode-binary-to-base64");
        var bytes = Convert.FromHexString(testCase.Input.GetProperty("bytes_hex").GetString()!);

        foreach (var scenario in testCase.Input.GetProperty("cases").EnumerateArray())
        {
            var contentType = scenario.GetProperty("content_type");

            var (text, base64) = ResponseDecoding.DecodeBody(
                bytes, contentType.ValueKind == JsonValueKind.Null ? null : contentType.GetString());

            Assert.Equal(testCase.Expected.GetProperty("body_text").GetString(), text);
            Assert.Equal(testCase.Expected.GetProperty("body_base64").GetString(), base64);
        }
    }

    [Fact]
    public void An_undeclared_text_body_is_text_and_vendor_json_is_recognised()
    {
        var testCase = Vector("decode-undeclared-text-and-vendor-json");

        var body = testCase.Input.GetProperty("decode_body");
        var (text, base64) = ResponseDecoding.DecodeBody(
            Encoding.UTF8.GetBytes(body.GetProperty("bytes_utf8").GetString()!), null);

        Assert.Equal(testCase.Expected.GetProperty("decode_body").GetProperty("body_text").GetString(), text);
        Assert.Null(base64);

        // Matched by suffix, not by a list: every vendor type in the wild is a +json or a +xml, and
        // a list would be wrong the day after it was written.
        foreach (var expected in testCase.Expected.GetProperty("is_textual_type_results").EnumerateObject())
        {
            Assert.Equal(expected.Value.GetBoolean(), ResponseDecoding.IsTextual(expected.Name));
        }
    }

    [Fact]
    public void A_declared_latin1_body_is_transcoded_rather_than_replaced()
    {
        var testCase = Vector("decode-latin1-transcode");

        var (text, base64) = ResponseDecoding.DecodeBody(
            Convert.FromHexString(testCase.Input.GetProperty("bytes_hex").GetString()!),
            testCase.Input.GetProperty("content_type").GetString());

        Assert.Equal(testCase.Expected.GetProperty("body_text").GetString(), text);
        Assert.Null(base64);
    }

    [Theory]
    [InlineData("text/plain; charset=UTF-8", "utf-8")]
    [InlineData("text/plain;charset=\"ISO-8859-1\"", "iso-8859-1")]
    [InlineData("text/plain", null)]
    public void The_charset_parameter_is_read_lowercased_and_unquoted(string contentType, string? expected) =>
        Assert.Equal(expected, ResponseDecoding.Charset(contentType));

    [Fact]
    public void A_nul_byte_is_what_marks_an_undeclared_body_binary()
    {
        Assert.True(ResponseDecoding.LooksBinary([0x41, 0x00, 0x42]));
        Assert.False(ResponseDecoding.LooksBinary("plain text"u8));

        // Only the first 4096 bytes are examined, so a large download is not scanned twice — which
        // does mean a NUL past that point is missed, and the body is shown as text.
        var late = new byte[5000];
        Array.Fill(late, (byte)'x');
        late[4999] = 0;
        Assert.False(ResponseDecoding.LooksBinary(late));

        var early = new byte[5000];
        Array.Fill(early, (byte)'x');
        early[4095] = 0;
        Assert.True(ResponseDecoding.LooksBinary(early));
    }

    private static FixtureCase Vector(string caseId) =>
        FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, Vectors))
            .SelectMany(f => f.Cases)
            .Single(c => c.Id == caseId);
}
