using CodeFlow.Update;
using Xunit;

namespace CodeFlow.Tests.Update;

/// <summary>
/// Deciding whether a release tag is newer than the running build.
/// </summary>
/// <remarks>
/// CodeFlow 1.7.2 never needed this: an in-webview updater compared versions itself. So there are
/// no extracted vectors, and these pin the behaviour the app depends on rather than a ported one.
/// </remarks>
public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.7.3", "1.7.2")]
    [InlineData("1.8.0", "1.7.9")]
    [InlineData("2.0.0", "1.99.99")]
    public void A_higher_version_is_newer(string candidate, string current) =>
        Assert.True(ReleaseVersion.IsNewer(candidate, current));

    [Fact]
    public void Ten_is_newer_than_two()
    {
        // The whole reason this is not a string comparison: "1.7.10" < "1.7.2" lexicographically,
        // and an app that believed that would sit on an old build reporting itself current.
        Assert.True(ReleaseVersion.IsNewer("1.7.10", "1.7.2"));
        Assert.False(ReleaseVersion.IsNewer("1.7.2", "1.7.10"));
    }

    [Theory]
    [InlineData("v1.7.2", "1.7.2")]
    [InlineData("1.7.2", "v1.7.2")]
    [InlineData("V1.7.2", "1.7.2")]
    public void The_tag_prefix_is_not_part_of_the_version(string candidate, string current) =>
        Assert.False(ReleaseVersion.IsNewer(candidate, current));

    [Fact]
    public void A_missing_segment_counts_as_zero() =>
        // Otherwise a release tagged `v1.8` would look older than `1.8.0` and never be offered.
        Assert.Equal(0, ReleaseVersion.Compare("1.8", "1.8.0"));

    [Fact]
    public void A_prerelease_is_older_than_the_release_it_precedes()
    {
        Assert.True(ReleaseVersion.IsNewer("1.8.0", "1.8.0-rc.1"));
        Assert.False(ReleaseVersion.IsNewer("1.8.0-rc.1", "1.8.0"));
    }

    [Fact]
    public void A_prerelease_still_beats_an_older_release() =>
        Assert.True(ReleaseVersion.IsNewer("1.8.0-rc.1", "1.7.2"));

    [Fact]
    public void Build_metadata_does_not_change_the_answer() =>
        Assert.Equal(0, ReleaseVersion.Compare("1.7.2+build.5", "1.7.2"));

    [Fact]
    public void A_tag_nobody_here_chose_the_shape_of_does_not_throw()
    {
        // A tag like `nightly` should make the check answer "nothing to offer", not take the
        // update panel down with an exception.
        Assert.False(ReleaseVersion.IsNewer("nightly", "1.7.2"));
        Assert.False(ReleaseVersion.IsNewer("", "1.7.2"));
    }
}
