using CodeFlow.Git;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>Remote listing and URL rewriting (GIT-026).</summary>
public sealed class RemotesTests
{
    [Fact]
    public void A_repository_with_no_remotes_lists_none()
    {
        using var repo = new TempRepo();

        Assert.Empty(Remotes.List(repo.Path));
    }

    [Fact]
    public void Setting_a_url_writes_both_the_fetch_and_the_push_url()
    {
        // Both, always. A repository that deliberately fetched and pushed to different URLs loses
        // that split, because this surface cannot express it — preserved, not corrected.
        using var repo = new TempRepo();
        using (var handle = repo.Open())
        {
            handle.Network.Remotes.Add("origin", "https://example.invalid/one.git");
            handle.Network.Remotes.Update("origin", r => r.PushUrl = "https://example.invalid/push-somewhere-else.git");
        }

        Remotes.SetUrl(repo.Path, "origin", "https://example.invalid/two.git");

        Assert.Equal([new RemoteInfo("origin", "https://example.invalid/two.git")], Remotes.List(repo.Path));

        using var check = repo.Open();
        Assert.Equal("https://example.invalid/two.git", check.Network.Remotes["origin"].PushUrl);
    }

    [Fact]
    public void Every_configured_remote_is_listed()
    {
        using var repo = new TempRepo();
        using (var handle = repo.Open())
        {
            handle.Network.Remotes.Add("origin", "https://example.invalid/one.git");
            handle.Network.Remotes.Add("upstream", "https://example.invalid/two.git");
        }

        Assert.Equal(
            [new RemoteInfo("origin", "https://example.invalid/one.git"),
             new RemoteInfo("upstream", "https://example.invalid/two.git")],
            Remotes.List(repo.Path).OrderBy(r => r.Name, StringComparer.Ordinal).ToList());
    }
}
