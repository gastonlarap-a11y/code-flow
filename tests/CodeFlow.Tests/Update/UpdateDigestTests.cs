using CodeFlow.Update;
using Xunit;

namespace CodeFlow.Tests.Update;

/// <summary>
/// Reading a digest out of the file a release publishes beside each installer.
/// See <c>docs/business-rules/02-bootstrap-platform.md</c>, <c>BOOT-021</c>.
/// </summary>
/// <remarks>
/// The format is whatever <c>shasum -a 256</c> and <c>sha256sum</c> agree on, because those are the
/// two commands that write it — <c>publish-release.sh</c> on macOS and the release workflow on
/// Windows. They differ in ways that matter here: GNU marks a binary read with a <c>*</c> before
/// the name, and neither strips the directory it was handed.
/// </remarks>
public sealed class UpdateDigestTests
{
    private const string Digest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void The_digest_is_read_from_a_plain_line()
    {
        Assert.Equal(Digest, UpdateService.DigestFor($"{Digest}  CodeFlow-1.7.5-arm64.dmg\n", "CodeFlow-1.7.5-arm64.dmg"));
    }

    [Fact]
    public void A_binary_marker_is_not_part_of_the_name()
    {
        // `sha256sum` on Windows writes `<hex> *<name>` for a binary read. Treating the asterisk as
        // part of the filename would fail to match every Windows installer ever published.
        Assert.Equal(Digest, UpdateService.DigestFor($"{Digest} *CodeFlow Setup 1.7.5.exe\n", "CodeFlow Setup 1.7.5.exe"));
    }

    [Fact]
    public void A_recorded_directory_is_not_part_of_the_name()
    {
        // Both commands record the path they were given, and a build script that forgets to `cd`
        // first records `dist-installers/…`. Comparing the last segment survives that.
        Assert.Equal(Digest, UpdateService.DigestFor($"{Digest}  dist-installers/CodeFlow-1.7.5-arm64.dmg\n", "CodeFlow-1.7.5-arm64.dmg"));
    }

    [Fact]
    public void A_name_with_spaces_survives()
    {
        // electron-builder's Windows default is `CodeFlow Setup <version>.exe`. Splitting on the
        // last space rather than the first would cut it in half.
        Assert.Equal(Digest, UpdateService.DigestFor($"{Digest}  CodeFlow Setup 1.7.5.exe\n", "CodeFlow Setup 1.7.5.exe"));
    }

    [Fact]
    public void A_single_entry_file_is_trusted_without_matching_the_name()
    {
        // The real v1.7.5 content, verbatim. GitHub rewrote the asset name's spaces to dots when it
        // stored it, so the API answers `CodeFlow.1.7.5.exe` while sha256sum recorded
        // `CodeFlow 1.7.5.exe`. Requiring the two to match refused every Windows update as
        // unverifiable — the failure this case exists to keep out.
        //
        // The file was fetched as `<asset>.sha256`, so it is already bound to that artefact by its
        // own name; the name inside is a second opinion, not the contract.
        const string real = "2adcf090ea3e4fb9a5700e25403405fcaad1c7b7c4e48b642c90b518e7696eac *CodeFlow 1.7.5.exe\n";

        Assert.Equal(
            "2adcf090ea3e4fb9a5700e25403405fcaad1c7b7c4e48b642c90b518e7696eac",
            UpdateService.DigestFor(real, "CodeFlow.1.7.5.exe"));
    }

    [Fact]
    public void The_right_line_is_picked_out_of_several()
    {
        var other = new string('a', 64);
        var file = $"{other}  CodeFlow-1.7.5-arm64.zip\n{Digest}  CodeFlow-1.7.5-arm64.dmg\n";

        Assert.Equal(Digest, UpdateService.DigestFor(file, "CodeFlow-1.7.5-arm64.dmg"));
    }

    [Fact]
    public void A_multi_entry_file_that_does_not_list_the_asset_yields_nothing()
    {
        // Only applies once there is more than one entry to choose between; a single-entry file is
        // trusted by the asset name it was fetched under. Returning some other line's digest, or an
        // empty string that then compares equal to nothing, would both be worse than null.
        var other = new string('b', 64);
        var file = $"{Digest}  something-else.dmg\n{other}  another.dmg\n";

        Assert.Null(UpdateService.DigestFor(file, "CodeFlow-1.7.5-arm64.dmg"));
    }

    [Fact]
    public void An_empty_file_yields_nothing()
    {
        Assert.Null(UpdateService.DigestFor(string.Empty, "CodeFlow-1.7.5-arm64.dmg"));
    }

    [Fact]
    public void A_name_that_only_looks_similar_does_not_match()
    {
        // Prefix or suffix matching here would accept a digest belonging to a different artefact,
        // which is precisely the substitution this whole check exists to catch. Two entries, so the
        // single-entry shortcut does not apply.
        var other = new string('b', 64);
        var file = $"{Digest}  CodeFlow-1.7.5-arm64.dmg.blockmap\n{other}  unrelated.dmg\n";

        Assert.Null(UpdateService.DigestFor(file, "CodeFlow-1.7.5-arm64.dmg"));
    }
}
