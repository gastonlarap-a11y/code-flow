using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeFlow.Files;
using CodeFlow.Ipc;

namespace CodeFlow.Update;

/// <summary>
/// Asking GitHub whether there is a newer CodeFlow, and fetching it.
/// </summary>
/// <remarks>
/// <para>
/// There is no signed manifest to check against: the repository is private, so nothing can be
/// fetched anonymously, and the app is unsigned, so there would be no key to verify an update
/// with. What is left is the honest subset — read the release list with a borrowed credential, and
/// hand the artefact over.
/// </para>
/// <para>
/// The repository is a constant. It is tempting to reuse <c>Providers.RepoDetection</c>, which
/// already parses a git remote, but that would point the app's update check at whatever project the
/// user happens to have open.
/// </para>
/// </remarks>
/// <param name="host">
/// Which keychain entry the release credential is read from. Defaults to the only value the app
/// ever uses; it is a parameter so a test can name a host of its own.
/// <para>
/// Without it there is no way to exercise the download at all: <c>UpdateCredential</c> reads the
/// keychain under this key, so a test would have to write — and then delete — the user's real
/// GitHub token to run. A unique <c>.invalid</c> host keeps the test off the real credential and
/// off the <c>gh</c> fallback, which would otherwise make it pass or fail by machine.
/// </para>
/// </param>
/// <param name="handOff">
/// What to do with the verified artefact. Defaults to the real thing — run the installer on
/// Windows, reveal it in Finder on macOS.
/// <para>
/// A test cannot use that: on Windows it would <em>launch the installer</em> on the machine running
/// the suite, which is the CI runner. Without this the happy path is untestable, and a suite that
/// only covers the refusals would still pass if the method learned to refuse everything.
/// </para>
/// </param>
internal sealed class UpdateService(
    HttpClient http,
    PublishEvent publish,
    string currentVersion,
    string host = "github.com",
    Action<string>? handOff = null)
{
    /// <summary>Where CodeFlow's own releases live. Not the open project's repository.</summary>
    private const string Owner = "gastonlarap-a11y";
    private const string Repo = "code-flow";

    /// <summary>How much of the download passes before another progress event is worth sending.</summary>
    /// <remarks>
    /// An event per chunk would be tens of thousands of IPC messages for a 150 MB installer, all to
    /// move a progress bar the eye cannot follow. 256 KiB is roughly a percent of one.
    /// </remarks>
    private const long ProgressInterval = 256 * 1024;

    /// <summary>
    /// The suffix of the asset carrying an artefact's SHA-256.
    /// </summary>
    /// <remarks>
    /// One digest file per artefact rather than a single shared <c>SHA256SUMS</c>, because the two
    /// installers are built on different machines at different times — the macOS <c>.dmg</c> by
    /// <c>publish-release.sh</c> and the Windows <c>.exe</c> by the release workflow — and a shared
    /// file would be two uploads racing to clobber each other. Each side publishes only its own.
    /// The name is a contract with both of them.
    /// </remarks>
    private const string DigestSuffix = ".sha256";

    public async Task<UpdateAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        var token = await UpdateCredential.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return Unavailable("no-credential");
        }

        using var request = Request(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest",
            token,
            "application/vnd.github+json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // No network, a laptop that just woke up, GitHub having a bad minute. The store only
            // shows this for a check the user asked for.
            return Unavailable("unreachable");
        }

        using (response)
        {
            // 404 is what a private repository returns to a token that cannot see it — GitHub hides
            // existence rather than admitting a permission failure — and also what a repository with
            // no releases yet returns. The two are indistinguishable from here, and today it is the
            // second: there are no releases.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Unavailable("unauthorized");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Unavailable("no-release");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = Deserialize(body);

            if (release is null || release.Draft)
            {
                return Unavailable("no-release");
            }

            if (!ReleaseVersion.IsNewer(release.TagName, currentVersion))
            {
                return Unavailable(string.Empty);
            }

            var asset = UpdateAssets.For(release.Assets ?? []);
            if (asset is null)
            {
                // A release exists and is newer, but carries nothing this platform can install —
                // which is what a Windows-only release looks like from a Mac. Saying "up to date"
                // here would be false.
                return Unavailable("no-asset") with { Version = release.TagName };
            }

            return new UpdateAvailability(
                Available: true,
                CurrentVersion: currentVersion,
                Version: release.TagName.TrimStart('v', 'V'),
                Notes: release.Body ?? string.Empty,
                Date: release.PublishedAt ?? string.Empty,
                AssetName: asset.Name,
                AssetUrl: asset.Url,
                AssetSize: asset.Size,
                InstallKind: UpdateAssets.InstallKind());
        }
    }

    /// <summary>
    /// Downloads the artefact, verifies its digest, and hands it to the platform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The asset is fetched through its API URL with <c>Accept: application/octet-stream</c>, which
    /// is the only way to read an asset of a private repository — <c>browser_download_url</c>
    /// answers a login page to a token-bearing request.
    /// </para>
    /// <para>
    /// Nothing about the artefact was checked before: on Windows the NSIS installer is launched
    /// straight after the download, unsigned, so anything that could put bytes on that response —
    /// a compromised release pipeline, a stolen token with write access — got code execution with
    /// no second opinion. TLS proves who served it, not what they served.
    /// </para>
    /// <para>
    /// A signature would be better and needs a certificate the project does not have. A digest
    /// published as its own release asset is what is available: it moves the trust from "whatever
    /// this response contained" to "the bytes the release recorded", so tampering has to beat two
    /// uploads rather than one. A release with no <c>SHA256SUMS</c> is refused rather than
    /// installed unverified — an update is the one download the app runs by itself.
    /// </para>
    /// </remarks>
    public async Task<UpdateInstallation> DownloadAsync(
        string assetUrl, string assetName, CancellationToken cancellationToken)
    {
        var token = await UpdateCredential.ResolveAsync(host, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("there is no GitHub credential to download the update with");

        var expected = await ExpectedDigestAsync(assetName, token, cancellationToken).ConfigureAwait(false);

        using var request = Request(HttpMethod.Get, assetUrl, token, "application/octet-stream");
        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // Downloads, not a temp directory: a manual install means the user goes looking for the
        // file, and the folder their browser would have used is where they will look.
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, assetName);

        var total = response.Content.Headers.ContentLength ?? 0;
        await WriteWithProgressAsync(response, target, total, cancellationToken).ConfigureAwait(false);

        await VerifyDigestAsync(target, expected, cancellationToken).ConfigureAwait(false);

        (handOff ?? Hand)(target);

        return new UpdateInstallation(target, UpdateAssets.InstallKind());
    }

    /// <summary>
    /// The digest the release recorded for <paramref name="assetName"/>.
    /// </summary>
    /// <remarks>
    /// Read from the release rather than carried in <see cref="UpdateAvailability"/> on purpose:
    /// the frontend passes the asset url and name back into <c>update_download</c>, and a digest
    /// that made the same round trip would be a digest an attacker who reached the renderer could
    /// choose. Fetching it here keeps both halves on this side of the boundary.
    /// </remarks>
    private async Task<string> ExpectedDigestAsync(
        string assetName, string token, CancellationToken cancellationToken)
    {
        using var request = Request(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest",
            token,
            "application/vnd.github+json");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var release = Deserialize(body)
            ?? throw new InvalidOperationException("the release list could not be read to verify the download");

        var wanted = assetName + DigestSuffix;
        var checksums = (release.Assets ?? []).FirstOrDefault(a =>
            a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"this release publishes no {wanted}, so the download cannot be verified and will not be installed");

        using var digestRequest = Request(HttpMethod.Get, checksums.Url, token, "application/octet-stream");
        using var digestResponse = await http.SendAsync(digestRequest, cancellationToken).ConfigureAwait(false);
        digestResponse.EnsureSuccessStatusCode();

        var text = await digestResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return DigestFor(text, assetName)
            ?? throw new InvalidOperationException(
                $"{wanted} lists no digest for {assetName}, so it cannot be verified and will not be installed");
    }

    /// <summary>
    /// Finds one file's digest in <c>sha256sum</c> output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The format is <c>&lt;hex&gt;  &lt;name&gt;</c>, two spaces, one line per file, and GNU marks
    /// a binary read with a <c>*</c> before the name. The name is read by its last path segment
    /// because both tools record whatever path they were handed.
    /// </para>
    /// <para>
    /// **A single-entry file yields its digest without checking the name at all**, and that is the
    /// important case. The caller fetched this file as <c>&lt;asset&gt;.sha256</c>, so the binding
    /// between digest and artefact is already the asset's own name; the name inside is a second
    /// opinion, not the contract.
    /// </para>
    /// <para>
    /// Insisting on it broke v1.7.5 on Windows. GitHub rewrites spaces to dots when it stores a
    /// release asset, so the API answered <c>CodeFlow.1.7.5.exe</c> while the digest file recorded
    /// <c>CodeFlow 1.7.5.exe</c> — the names never matched, and every Windows update was refused as
    /// unverifiable. The artefacts are named without spaces now, but a verifier whose failure mode
    /// is "silently refuse every update" must not depend on that holding forever.
    /// </para>
    /// </remarks>
    internal static string? DigestFor(string checksums, string assetName)
    {
        var entries = checksums
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => (Separator: line.IndexOf(' ', StringComparison.Ordinal), Line: line))
            .Where(e => e.Separator > 0)
            .Select(e => (Digest: e.Line[..e.Separator].Trim(), Name: e.Line[e.Separator..].Trim().TrimStart('*')))
            .ToList();

        if (entries.Count == 1)
        {
            return entries[0].Digest;
        }

        foreach (var entry in entries)
        {
            if (Path.GetFileName(entry.Name).Equals(assetName, StringComparison.Ordinal))
            {
                return entry.Digest;
            }
        }

        return null;
    }

    /// <summary>Hashes what landed on disk and refuses anything that does not match.</summary>
    /// <remarks>
    /// The file is deleted on a mismatch. Leaving a rejected installer in the user's Downloads
    /// folder means the app declined to run it and then left it one double-click away.
    /// </remarks>
    private static async Task VerifyDigestAsync(
        string target, string expected, CancellationToken cancellationToken)
    {
        string actual;
        await using (var stream = File.OpenRead(target))
        {
            actual = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected.Trim().ToLowerInvariant())))
        {
            File.Delete(target);
            throw new InvalidOperationException(
                "the downloaded update does not match the digest the release published, so it was discarded");
        }
    }

    private async Task WriteWithProgressAsync(
        HttpResponseMessage response, string target, long total, CancellationToken cancellationToken)
    {
        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long downloaded = 0;
        long lastReported = 0;

        await PublishProgressAsync(0, total, done: false, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;

            if (downloaded - lastReported >= ProgressInterval)
            {
                lastReported = downloaded;
                await PublishProgressAsync(downloaded, total, done: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await PublishProgressAsync(downloaded, total, done: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives the artefact to the platform in the only way that platform allows.</summary>
    /// <remarks>
    /// Windows launches the NSIS installer, which waits for the app to exit and replaces it — the
    /// renderer then offers a restart. macOS only opens the disk image: replacing a running,
    /// unsigned <c>.app</c> in place produces a bundle Gatekeeper has no record of. See
    /// <see cref="UpdateAssets.InstallKind"/>.
    /// </remarks>
    private static void Hand(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

            return;
        }

        FileOps.RevealInFileManager(path);
    }

    private ValueTask PublishProgressAsync(
        long downloaded, long total, bool done, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(
            new UpdateProgress(downloaded, total, done), UpdateJsonContext.Default.UpdateProgress);

        return publish("update:progress", payload, cancellationToken);
    }

    private UpdateAvailability Unavailable(string reason) =>
        new(Available: false, CurrentVersion: currentVersion, Reason: reason);

    private static ReleasePayload? Deserialize(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, UpdateJsonContext.Default.ReleasePayload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string token, string accept)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        // GitHub rejects a request with no User-Agent outright, and pins the response shape to the
        // API version rather than to whatever is current when this runs.
        request.Headers.UserAgent.Add(UserAgent());
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        return request;
    }

    /// <summary>How the app names itself to GitHub.</summary>
    /// <remarks>
    /// Built from the running version rather than a literal, which one release would have made a
    /// lie. <see cref="ProductInfoHeaderValue"/> throws on anything that is not a valid HTTP token,
    /// and the version arrives from a command-line argument — so a malformed one degrades to the
    /// bare product name. Losing the version off a header nobody reads is not worth taking the
    /// update check down for.
    /// </remarks>
    private ProductInfoHeaderValue UserAgent()
    {
        try
        {
            return new ProductInfoHeaderValue("CodeFlow", currentVersion);
        }
        catch (FormatException)
        {
            return new ProductInfoHeaderValue(new ProductHeaderValue("CodeFlow"));
        }
    }
}
