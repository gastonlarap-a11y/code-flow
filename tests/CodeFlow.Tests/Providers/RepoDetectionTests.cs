using CodeFlow.Providers;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// Recognising a repository from its git remote.
/// See <c>docs/business-rules/06-providers.md</c> <c>PROV-003</c>.
/// </summary>
/// <remarks>
/// No vectors were extracted for these two functions, so unlike <see cref="PrLinkTests"/> these
/// assertions are read off 1.7.2's own source rather than replayed from its tests. Each case
/// below names the shape it comes from for that reason.
/// </remarks>
public sealed class RepoDetectionTests
{
    private static readonly string[] KnownHosts = ["github.com", "ghe.contoso.com"];

    // ---------- GitHub ----------

    [Theory]
    [InlineData("https://github.com/acme/widget.git")]
    [InlineData("https://github.com/acme/widget")]
    [InlineData("https://github.com/acme/widget/")]
    [InlineData("ssh://git@github.com/acme/widget.git")]
    [InlineData("git@github.com:acme/widget.git")]
    [InlineData("https://user:token@github.com/acme/widget.git")]
    public void Every_shape_a_github_remote_comes_in_resolves_to_the_same_repo(string remote)
    {
        var detected = RepoDetection.GitHub(remote, KnownHosts);

        Assert.NotNull(detected);
        Assert.Equal("github.com", detected.Host);
        Assert.Equal("acme", detected.Owner);
        Assert.Equal("widget", detected.Repo);
    }

    [Fact]
    public void An_unknown_host_is_not_assumed_to_be_github_enterprise()
    {
        // Without the allow-list a GitLab, Bitbucket or self-hosted remote would be indistinguishable
        // from an Enterprise one — so an unconnected host falls back to linking by hand.
        Assert.Null(RepoDetection.GitHub("https://gitlab.com/acme/widget.git", KnownHosts));
        Assert.Null(RepoDetection.GitHub("https://ghe.unknown.com/team/app.git", KnownHosts));

        Assert.NotNull(RepoDetection.GitHub("https://ghe.contoso.com/team/app.git", KnownHosts));
    }

    [Fact]
    public void The_detected_host_takes_the_spelling_that_was_saved()
    {
        // It becomes part of the keychain key, so it has to match what the user connected rather than
        // how they happened to type the remote.
        var detected = RepoDetection.GitHub("https://GitHub.COM/acme/widget.git", KnownHosts);

        Assert.NotNull(detected);
        Assert.Equal("github.com", detected.Host);
    }

    [Fact]
    public void A_deeper_github_path_keeps_only_the_first_two_segments()
    {
        var detected = RepoDetection.GitHub("https://github.com/acme/widget/tree/main", KnownHosts);

        Assert.NotNull(detected);
        Assert.Equal("widget", detected.Repo);
    }

    [Theory]
    [InlineData("https://github.com/acme")]
    [InlineData("github.com")]
    [InlineData("")]
    public void A_remote_with_no_owner_and_repo_is_not_recognised(string remote) =>
        Assert.Null(RepoDetection.GitHub(remote, KnownHosts));

    // ---------- Azure DevOps ----------

    [Fact]
    public void The_modern_azure_https_remote_resolves()
    {
        var detected = RepoDetection.Azure("https://dev.azure.com/contoso/Marketing%20Website/_git/site");

        Assert.NotNull(detected);
        Assert.Equal("contoso", detected.Org);
        // %20 becomes a space; nothing else is decoded, because 1.7.2's own decoder is a
        // single replace rather than a percent-decoder.
        Assert.Equal("Marketing Website", detected.Project);
        Assert.Equal("site", detected.Repo);
    }

    [Fact]
    public void The_legacy_visualstudio_remote_resolves_with_and_without_the_collection()
    {
        foreach (var remote in new[]
                 {
                     "https://contoso.visualstudio.com/Web/_git/api",
                     "https://contoso.visualstudio.com/DefaultCollection/Web/_git/api",
                 })
        {
            var detected = RepoDetection.Azure(remote);

            Assert.NotNull(detected);
            Assert.Equal("contoso", detected.Org);
            Assert.Equal("Web", detected.Project);
            Assert.Equal("api", detected.Repo);
        }
    }

    [Fact]
    public void The_one_azure_ssh_shape_is_an_exact_prefix_and_exactly_three_segments()
    {
        var detected = RepoDetection.Azure("git@ssh.dev.azure.com:v3/contoso/Web/api");

        Assert.NotNull(detected);
        Assert.Equal("contoso", detected.Org);
        Assert.Equal("Web", detected.Project);
        Assert.Equal("api", detected.Repo);

        // Four segments is not the shape, so it is not recognised at all rather than truncated.
        Assert.Null(RepoDetection.Azure("git@ssh.dev.azure.com:v3/contoso/Web/api/extra"));
        // And the prefix is literal: no other SSH host is accepted, including the one that looks like
        // it should be.
        Assert.Null(RepoDetection.Azure("git@vs-ssh.visualstudio.com:v3/contoso/Web/api"));
    }

    [Fact]
    public void An_azure_path_that_is_not_exactly_the_expected_shape_is_rejected()
    {
        // Unlike the GitHub path, each Azure shape is matched on an exact segment count: an extra or
        // missing segment means unrecognised, never a best guess.
        Assert.Null(RepoDetection.Azure("https://dev.azure.com/contoso/Web/_git/api/extra"));
        Assert.Null(RepoDetection.Azure("https://dev.azure.com/contoso/_git/api"));
        Assert.Null(RepoDetection.Azure("https://dev.azure.com/contoso/Web/api"));
        Assert.Null(RepoDetection.Azure("https://contoso.visualstudio.com/_git/api"));
    }

    [Fact]
    public void An_azure_remote_with_no_scheme_is_rejected()
    {
        // The scp-like form is accepted for GitHub but not here — 1.7.2 requires http(s) for
        // everything except the one SSH prefix above.
        Assert.Null(RepoDetection.Azure("git@dev.azure.com:contoso/Web/_git/api"));
        Assert.Null(RepoDetection.Azure("dev.azure.com/contoso/Web/_git/api"));
    }

    [Fact]
    public void The_legacy_organisation_keeps_the_hosts_own_casing()
    {
        // It becomes part of the keychain key, and 1.7.2 does not fold it — so neither does
        // this. (`dev.azure.com` is matched case-insensitively, but the org there comes from the path,
        // so the question does not arise.)
        Assert.Equal("Contoso", RepoDetection.Azure("https://Contoso.visualstudio.com/Web/_git/api")?.Org);
    }

    [Fact]
    public void The_legacy_host_suffix_is_matched_case_sensitively()
    {
        // `strip_suffix(".visualstudio.com")` in 1.7.2, not a case-insensitive compare — so a
        // remote whose *suffix* is capitalised is not recognised. Almost certainly an oversight there
        // rather than a decision, but it is observable behaviour and this pins it rather than quietly
        // improving on it.
        Assert.Null(RepoDetection.Azure("https://contoso.VisualStudio.com/Web/_git/api"));
    }

    [Fact]
    public void A_github_remote_is_not_mistaken_for_an_azure_one_and_the_reverse()
    {
        Assert.Null(RepoDetection.Azure("https://github.com/acme/widget.git"));
        Assert.Null(RepoDetection.GitHub("https://dev.azure.com/contoso/Web/_git/api", KnownHosts));
    }

    [Fact]
    public void Every_trailing_git_suffix_is_removed_not_just_one()
    {
        // Repeated suffix trimming is repeated by definition. Silly input, but reproducing it costs a
        // loop and diverging costs a mismatch nobody would look for.
        Assert.Equal("widget", RepoDetection.GitHub("https://github.com/acme/widget.git.git", KnownHosts)?.Repo);
        Assert.Equal("api", RepoDetection.Azure("https://dev.azure.com/contoso/Web/_git/api.git.git")?.Repo);
    }
}
