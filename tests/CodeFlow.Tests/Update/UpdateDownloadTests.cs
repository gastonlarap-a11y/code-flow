using System.Security.Cryptography;
using System.Text.Json;
using CodeFlow.Update;
using CodeFlow.Tests.Providers;
using Xunit;

namespace CodeFlow.Tests.Update;

/// <summary>
/// Downloading an update and refusing one that does not match its published digest.
/// See <c>docs/business-rules/02-bootstrap-platform.md</c>, <c>BOOT-021</c>.
/// </summary>
/// <remarks>
/// <para>
/// The refusals are the point. On Windows the downloaded installer is launched straight after this
/// method returns, unsigned, so "it downloaded something" and "it downloaded the right thing" have
/// very different consequences and only one of them used to be true.
/// </para>
/// <para>
/// Two seams make this reachable without touching the machine it runs on. The service is given a
/// unique <c>.invalid</c> host, so the credential it reads is one this test wrote to the keychain
/// and not the user's real GitHub token — and no <c>gh</c> fallback is consulted, which would
/// otherwise decide the result by machine. And the hand-off is replaced, because the real one runs
/// the installer, and the CI runner is Windows.
/// </para>
/// </remarks>
// `TempGitHubToken` writes to the real OS credential store, so this class contends for the same
// resource as `CredentialStoreTests` — on macOS the login keychain, reached through both the
// Security framework and the `security` CLI. Left out of the collection it made that suite fail
// intermittently, which is precisely what SerialKeychain exists to reduce.
[Collection(SerialKeychain.Name)]
public sealed class UpdateDownloadTests : IDisposable
{
    private const string AssetUrl = "https://api.github.com/repos/gastonlarap-a11y/code-flow/releases/assets/1";

    /// <summary>Unique per test run: <c>DownloadAsync</c> writes to the real Downloads folder.</summary>
    private readonly string _assetName = $"codeflow-test-{Guid.NewGuid():N}.dmg";

    private readonly List<string> _handedOff = [];

    /// <summary>A host no real token can be filed under, so the user's keychain is untouched.</summary>
    private readonly string _host = TempGitHubToken.UniqueHost();

    private string Target => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", _assetName);

    public void Dispose()
    {
        // The refusal cases delete it themselves; this is for the case that does not.
        if (File.Exists(Target))
        {
            File.Delete(Target);
        }
    }

    [Fact]
    public async Task An_artefact_matching_its_digest_is_handed_over()
    {
        // Without this case the suite would still pass if the method learned to refuse everything.
        var installer = "pretend this is an installer"u8.ToArray();
        using var _ = new TempGitHubToken(_host);

        var service = Service(Handler(installer, Sha256(installer)));

        var installation = await service.DownloadAsync(AssetUrl, _assetName, TestContext.Current.CancellationToken);

        Assert.Equal(Target, installation.Path);
        Assert.True(File.Exists(Target));
        Assert.Equal(installer, await File.ReadAllBytesAsync(Target, TestContext.Current.CancellationToken));
        Assert.Equal([Target], _handedOff);
    }

    [Fact]
    public async Task An_artefact_that_does_not_match_its_digest_is_refused_and_deleted()
    {
        // The one case that distinguishes verifying from claiming to verify.
        var served = "this is not what the release recorded"u8.ToArray();
        using var _ = new TempGitHubToken(_host);

        var service = Service(Handler(served, Sha256("the bytes the release recorded"u8.ToArray())));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadAsync(AssetUrl, _assetName, TestContext.Current.CancellationToken));

        Assert.Contains("does not match the digest", failure.Message, StringComparison.Ordinal);

        // Deleted, not merely rejected: an installer the app refused to run must not be left sitting
        // in Downloads one double-click away from running anyway.
        Assert.False(File.Exists(Target));
        Assert.Empty(_handedOff);
    }

    [Fact]
    public async Task A_release_that_publishes_no_digest_is_refused_before_anything_is_downloaded()
    {
        using var _ = new TempGitHubToken(_host);

        var handler = new FakeHttpHandler().When("releases/latest", ReleaseJson(withDigestAsset: false));
        var service = Service(handler);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadAsync(AssetUrl, _assetName, TestContext.Current.CancellationToken));

        Assert.Contains("publishes no", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Target));

        // Refused before the transfer, not after: there is no reason to pull 150 MB that cannot be
        // checked when it lands.
        Assert.Equal(0, handler.CountFor("releases/assets"));
    }

    [Fact]
    public async Task A_digest_file_that_does_not_list_the_asset_is_refused()
    {
        var installer = "pretend this is an installer"u8.ToArray();
        using var _ = new TempGitHubToken(_host);

        // Two entries on purpose. A single-entry file is trusted by the asset name it was fetched
        // under — see `DigestFor` — so the name check this asserts only comes into play once there
        // is more than one line to choose between.
        var other = new string('b', 64);
        var handler = new FakeHttpHandler()
            .When("releases/latest", ReleaseJson(withDigestAsset: true))
            .When("assets/2", $"{Sha256(installer)}  some-other-artefact.dmg\n{other}  another.dmg\n")
            .WhenBytes("assets/1", installer);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(handler).DownloadAsync(AssetUrl, _assetName, TestContext.Current.CancellationToken));

        Assert.Contains("lists no digest for", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task The_digest_is_fetched_with_the_same_credential_as_the_artefact()
    {
        // The digest is only worth anything if it comes from the release rather than from whatever
        // answered. An unauthenticated request to a private repository answers a 404, not the file.
        var installer = "pretend this is an installer"u8.ToArray();
        using var _ = new TempGitHubToken(_host, "test-token");

        var handler = Handler(installer, Sha256(installer));
        await Service(handler).DownloadAsync(AssetUrl, _assetName, TestContext.Current.CancellationToken);

        var digestRequest = Assert.Single(handler.Requests, r => r.Uri.ToString().Contains("assets/2", StringComparison.Ordinal));
        Assert.Equal("Bearer test-token", digestRequest.Header("authorization"));
    }

    [Fact]
    public async Task The_digest_is_read_from_the_release_rather_than_from_the_caller()
    {
        // update_download takes the asset url and name from the renderer, which means an attacker who
        // reached the renderer chooses them. The digest must not come from the same place, so this
        // pins that it is fetched from the release listing on this side of the boundary.
        var installer = "pretend this is an installer"u8.ToArray();
        using var _ = new TempGitHubToken(_host);

        var handler = Handler(installer, Sha256(installer));
        await Service(handler).DownloadAsync(AssetUrl, _assetName, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CountFor("releases/latest"));
    }

    private UpdateService Service(FakeHttpHandler handler) =>
        new(new HttpClient(handler), (_, _, _) => ValueTask.CompletedTask, "1.7.4", _host, _handedOff.Add);

    /// <summary>The three responses a successful download makes: the release, the digest, the bytes.</summary>
    private FakeHttpHandler Handler(byte[] installer, string digest) =>
        new FakeHttpHandler()
            .When("releases/latest", ReleaseJson(withDigestAsset: true))
            .When("assets/2", $"{digest}  {_assetName}\n")
            .WhenBytes("assets/1", installer);

    private string ReleaseJson(bool withDigestAsset)
    {
        var assets = new List<object> { new { name = _assetName, url = AssetUrl, size = 4 } };
        if (withDigestAsset)
        {
            assets.Add(new
            {
                name = _assetName + ".sha256",
                url = "https://api.github.com/repos/gastonlarap-a11y/code-flow/releases/assets/2",
                size = 80,
            });
        }

        return JsonSerializer.Serialize(new
        {
            tag_name = "v1.7.5",
            name = "v1.7.5",
            body = "notes",
            published_at = "2026-08-01T00:00:00Z",
            draft = false,
            prerelease = false,
            assets,
        });
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
