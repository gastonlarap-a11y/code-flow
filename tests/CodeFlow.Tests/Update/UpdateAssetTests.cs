using System.Text.Json;
using CodeFlow.Update;
using Xunit;

namespace CodeFlow.Tests.Update;

/// <summary>
/// Which artefact on a release this machine should be offered, and what it can do with it.
/// </summary>
public sealed class UpdateAssetTests
{
    /// <summary>What `scripts/build-app.sh` produces for both platforms, on one release.</summary>
    private static readonly ReleaseAsset[] BothPlatforms =
    [
        new("CodeFlow-1.7.3-arm64.dmg", "https://api.github.com/…/1", 161_000_000),
        new("CodeFlow-1.7.3-arm64.dmg.sha256", "https://api.github.com/…/2", 91),
        new("CodeFlow-1.7.3-arm64-mac.zip", "https://api.github.com/…/3", 158_000_000),
        new("CodeFlow-Setup-1.7.3-x64.exe", "https://api.github.com/…/4", 149_000_000),
        new("CodeFlow-Setup-1.7.3-x64.exe.sha256", "https://api.github.com/…/5", 91),
        new("CodeFlow-Setup-1.7.3-x64.exe.blockmap", "https://api.github.com/…/6", 160_000),
    ];

    /// <summary>
    /// What a Windows release actually carries: an installer *and* a portable build.
    /// </summary>
    /// <remarks>
    /// The portable one is listed first, which is the order v1.7.5's release put them in — and
    /// picking whichever came first is the bug this fixture exists to keep out.
    /// </remarks>
    private static readonly ReleaseAsset[] WindowsWithPortable =
    [
        new("CodeFlow-Portable-1.7.6-x64.exe", "https://api.github.com/…/1", 143_000_000),
        new("CodeFlow-Portable-1.7.6-x64.exe.sha256", "https://api.github.com/…/2", 91),
        new("CodeFlow-Setup-1.7.6-x64.exe", "https://api.github.com/…/3", 143_000_000),
        new("CodeFlow-Setup-1.7.6-x64.exe.sha256", "https://api.github.com/…/4", 91),
    ];

    /// <summary>
    /// The installer is chosen, not merely the first <c>.exe</c>.
    /// </summary>
    /// <remarks>
    /// <c>InstallKind()</c> is <c>auto</c> on Windows, so the artefact this returns is executed.
    /// Handing over the portable build would launch a loose copy of the new version and leave the
    /// installed one exactly as it was — an update that appears to work and updates nothing.
    /// </remarks>
    [Fact]
    public void The_windows_installer_wins_over_the_portable_build()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows publishes a portable build beside its installer.");

        var chosen = UpdateAssets.For(WindowsWithPortable);

        Assert.NotNull(chosen);
        Assert.Equal("CodeFlow-Setup-1.7.6-x64.exe", chosen.Name);
    }

    /// <summary>A release built before the names were made explicit still offers its one installer.</summary>
    /// <remarks>
    /// Refusing to see an unmarked `.exe` would be a worse answer than offering it: v1.7.5 and
    /// everything before it names its installer `CodeFlow Setup <version>.exe`, with no marker.
    /// </remarks>
    [Fact]
    public void An_older_release_without_the_marker_is_still_offered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The fallback only applies to the Windows installer.");

        ReleaseAsset[] older =
        [
            new("CodeFlow.1.7.5.exe", "https://api.github.com/…/1", 142_000_000),
        ];

        Assert.Equal("CodeFlow.1.7.5.exe", UpdateAssets.For(older)?.Name);
    }

    /// <summary>A digest file is never mistaken for the artefact it describes.</summary>
    /// <remarks>
    /// It ends in its own extension, so it cannot match — but every release has carried one beside
    /// each artefact since v1.7.5, and "cannot match" is worth an assertion rather than a reading.
    /// </remarks>
    [Fact]
    public void A_sha256_file_is_never_chosen()
    {
        var chosen = UpdateAssets.For(BothPlatforms);

        Assert.NotNull(chosen);
        Assert.DoesNotContain(".sha256", chosen.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Each_platform_is_offered_its_own_installer()
    {
        var chosen = UpdateAssets.For(BothPlatforms);

        Assert.NotNull(chosen);
        Assert.EndsWith(OperatingSystem.IsWindows() ? ".exe" : ".dmg", chosen.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blockmap_is_never_mistaken_for_an_installer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only the Windows installer has a blockmap beside it.");

        // `Setup.exe.blockmap` also ends in a name containing ".exe", and downloading 160 KB of
        // delta index instead of the installer would fail in a way nothing here would explain.
        var chosen = UpdateAssets.For(BothPlatforms);

        Assert.NotNull(chosen);
        Assert.DoesNotContain(".blockmap", chosen.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_release_without_this_platform_offers_nothing()
    {
        // A Windows-only release seen from a Mac. Answering with the .exe would be worse than
        // answering with nothing.
        ReleaseAsset[] windowsOnly = [new("CodeFlow Setup 1.7.3.exe", "https://api.github.com/…/3", 1)];

        Assert.Equal(OperatingSystem.IsWindows(), UpdateAssets.For(windowsOnly) is not null);
    }

    [Fact]
    public void Only_windows_claims_it_can_install_on_its_own() =>
        // macOS is `manual` while the app is unsigned: replacing a running bundle in place leaves
        // one Gatekeeper has no record of. Promising a restart there is the same class of untruth
        // the old "only works in the installed app" message was.
        Assert.Equal(
            OperatingSystem.IsWindows() ? UpdateAssets.Auto : UpdateAssets.Manual,
            UpdateAssets.InstallKind());

    [Fact]
    public void A_github_release_payload_deserialises_by_its_own_names()
    {
        // The one shape read *from* GitHub rather than written to the renderer, so its names are
        // GitHub's and a naming policy applied to the outbound records must not touch it.
        const string Body = """
            {
              "tag_name": "v1.7.3",
              "name": "1.7.3",
              "body": "Fixes the thing.",
              "published_at": "2026-08-01T09:12:00Z",
              "draft": false,
              "prerelease": false,
              "assets": [{ "name": "CodeFlow-1.7.3-arm64.dmg", "url": "https://api.github.com/x", "size": 42 }]
            }
            """;

        var release = JsonSerializer.Deserialize(Body, UpdateJsonContext.Default.ReleasePayload);

        Assert.NotNull(release);
        Assert.Equal("v1.7.3", release.TagName);
        Assert.Equal("2026-08-01T09:12:00Z", release.PublishedAt);
        Assert.False(release.Draft);
        Assert.Equal(42, Assert.Single(release.Assets!).Size);
    }

    [Fact]
    public void An_unavailable_answer_carries_why_and_the_running_version()
    {
        // "up to date" and "I could not find out" are different answers, and the panel prints a
        // different sentence for each. An empty reason is the first; anything else is the second.
        var payload = JsonSerializer.Serialize(
            new UpdateAvailability(Available: false, CurrentVersion: "1.7.2", Reason: "no-credential"),
            UpdateJsonContext.Default.UpdateAvailability);

        Assert.Contains("\"available\":false", payload, StringComparison.Ordinal);
        Assert.Contains("\"current_version\":\"1.7.2\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"no-credential\"", payload, StringComparison.Ordinal);
    }
}
