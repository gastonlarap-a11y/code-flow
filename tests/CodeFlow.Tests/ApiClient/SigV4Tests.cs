using System.Text.Json;
using CodeFlow.ApiClient;
using CodeFlow.Tests.TestVectors;
using Xunit;

namespace CodeFlow.Tests.ApiClient;

/// <summary>
/// AWS Signature Version 4, against the vectors.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-011</c>–<c>API-015</c>.
/// </summary>
/// <remarks>
/// Two of these are AWS's own published <c>aws-sig-v4-test-suite</c> cases, byte-identical. A
/// signature is either exactly right or useless, so there is nothing to approximate here — which is
/// also why the encoding rules are written out rather than taken from
/// <see cref="Uri.EscapeDataString(string)"/>.
/// </remarks>
public sealed class SigV4Tests
{
    private const string Vectors = "http.vectors.json";

    [Fact]
    public void The_published_aws_get_vanilla_vector_is_reproduced()
    {
        var testCase = Vector("sigv4-get-vanilla");
        var input = testCase.Input;

        var headers = input.GetProperty("headers")
            .EnumerateArray()
            .Select(pair => (pair[0].GetString()!, pair[1].GetString()!))
            .ToList();

        var (signedHeaders, signature) = SigV4.Sign(
            new SigV4Request(
                input.GetProperty("method").GetString()!,
                input.GetProperty("canonical_uri").GetString()!,
                input.GetProperty("canonical_query").GetString()!,
                headers,
                input.GetProperty("payload_hash").GetString()!),
            new SigV4Credentials(
                string.Empty,
                input.GetProperty("secret_key").GetString()!,
                string.Empty,
                input.GetProperty("region").GetString()!,
                input.GetProperty("service").GetString()!),
            input.GetProperty("amz_date").GetString()!);

        Assert.Equal(testCase.Expected.GetProperty("signed_headers").GetString(), signedHeaders);
        Assert.Equal(testCase.Expected.GetProperty("signature").GetString(), signature);
    }

    [Fact]
    public void The_canonical_query_is_sorted_and_encoded_the_way_the_vector_says()
    {
        var testCase = Vector("canonical-query-sort-and-encode");

        var url = new Uri(testCase.Input.GetProperty("url").GetString()!);

        Assert.Equal(testCase.Expected.GetProperty("canonical_query").GetString(), SigV4.CanonicalQuery(url));
    }

    /// <summary>
    /// S3 keeps its path as it stands; everything else is encoded once.
    /// </summary>
    /// <remarks>
    /// An S3 key <em>is</em> the path, so encoding it again would sign a different object than the
    /// one being requested — and the service would answer with a signature mismatch rather than
    /// with anything that named the real problem.
    /// </remarks>
    [Theory]
    [InlineData("s3", "/my bucket/a+b.txt", "/my%20bucket/a+b.txt")]
    [InlineData("S3", "/my bucket/a+b.txt", "/my%20bucket/a+b.txt")]
    [InlineData("execute-api", "/my bucket/a+b.txt", "/my%2520bucket/a%2Bb.txt")]
    [InlineData("execute-api", "/plain", "/plain")]
    public void The_canonical_path_is_signed_once_for_s3_and_twice_for_everything_else(
        string service, string path, string expected) =>
        Assert.Equal(expected, SigV4.CanonicalUri(new Uri($"https://example.test{path}"), service));

    /// <summary>
    /// The encoder is SigV4's, not the framework's.
    /// </summary>
    /// <remarks>
    /// <see cref="Uri.EscapeDataString(string)"/> leaves <c>!*'()</c> literal, which AWS requires
    /// encoded. One character wrong is a rejected request with no useful diagnosis, so it is pinned
    /// rather than trusted.
    /// </remarks>
    [Fact]
    public void Characters_the_framework_would_leave_alone_are_encoded()
    {
        var canonical = SigV4.CanonicalQuery(new Uri("https://example.test/?a=!*'()"));

        Assert.Equal("a=%21%2A%27%28%29", canonical);
    }

    [Fact]
    public void A_repeated_header_is_signed_as_one_comma_joined_value_in_the_order_sent()
    {
        var (signedHeaders, _) = SigV4.Sign(
            new SigV4Request("GET", "/", string.Empty,
                [("host", "example.test"), ("x-amz-meta", "one"), ("x-amz-meta", "two")],
                SigV4.HexSha256([])),
            new SigV4Credentials("k", "s", string.Empty, "us-east-1", "service"),
            "20150830T123600Z");

        // One entry in the signed-header list, not two.
        Assert.Equal("host;x-amz-meta", signedHeaders);
    }

    [Theory]
    [InlineData("  a   b  ", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("plain", "plain")]
    public void A_header_value_is_normalised_to_single_spaces(string value, string expected) =>
        Assert.Equal(expected, SigV4.NormalizeValue(value));

    /// <summary>Headers the transport rewrites after signing are excluded from it.</summary>
    [Theory]
    [InlineData("content-length", true)]
    [InlineData("Content-Length", true)]
    [InlineData("authorization", true)]
    [InlineData("user-agent", true)]
    [InlineData("x-amz-date", false)]
    [InlineData("host", false)]
    public void The_headers_the_sdks_exclude_are_excluded_here_too(string name, bool excluded) =>
        Assert.Equal(excluded, SigV4.IsUnsignable(name));

    /// <summary>
    /// The header-building vector: what a signed request actually carries.
    /// </summary>
    /// <remarks>
    /// Proves <c>accept-encoding</c> is excluded from <c>SignedHeaders</c> while <c>host</c> and
    /// <c>x-amz-content-sha256</c> are included — and that no security-token header appears when
    /// there is no session token to put in one.
    /// </remarks>
    [Fact]
    public void A_signed_request_carries_the_headers_the_vector_lists()
    {
        var testCase = Vector("sigv4-headers-host-date-payload-hash");
        var input = testCase.Input;

        var headers = SigV4.Headers(
            input.GetProperty("method").GetString()!,
            new Uri(input.GetProperty("url").GetString()!),
            [.. input.GetProperty("headers").EnumerateArray()
                .Select(pair => (pair[0].GetString()!, pair[1].GetString()!))],
            input.GetProperty("payload_hash").GetString()!,
            new SigV4Credentials(
                input.GetProperty("access_key").GetString()!,
                input.GetProperty("secret_key").GetString()!,
                input.GetProperty("session_token").GetString()!,
                input.GetProperty("region").GetString()!,
                input.GetProperty("service").GetString()!),
            input.GetProperty("amz_date").GetString()!);

        foreach (var expected in testCase.Expected.GetProperty("headers_contains").EnumerateObject())
        {
            Assert.Equal(expected.Value.GetString(), Assert.Single(headers, h => h.Name == expected.Name).Value);
        }

        foreach (var absent in testCase.Expected.GetProperty("headers_absent").EnumerateArray())
        {
            Assert.DoesNotContain(headers, h => h.Name == absent.GetString());
        }
    }

    [Fact]
    public void A_session_token_is_carried_and_signed_when_there_is_one()
    {
        var headers = SigV4.Headers(
            "GET",
            new Uri("https://example.amazonaws.com/"),
            [],
            SigV4.HexSha256([]),
            new SigV4Credentials("AKIDEXAMPLE", "secret", "session-token", "us-east-1", "service"),
            "20150830T123600Z");

        Assert.Equal("session-token", Assert.Single(headers, h => h.Name == "x-amz-security-token").Value);
        Assert.Contains("x-amz-security-token", Assert.Single(headers, h => h.Name == "authorization").Value);
    }

    [Theory]
    [InlineData("", "secret", "AWS SigV4 needs both an access key and a secret key")]
    [InlineData("key", "", "AWS SigV4 needs both an access key and a secret key")]
    public void Signing_without_credentials_says_which_half_is_missing(
        string accessKey, string secretKey, string expected)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => SigV4.Headers(
            "GET", new Uri("https://example.amazonaws.com/"), [], SigV4.HexSha256([]),
            new SigV4Credentials(accessKey, secretKey, string.Empty, "us-east-1", "service"),
            "20150830T123600Z"));

        Assert.Equal(expected, failure.Message);
    }

    private static FixtureCase Vector(string caseId) =>
        FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, Vectors))
            .SelectMany(f => f.Cases)
            .Single(c => c.Id == caseId);
}
