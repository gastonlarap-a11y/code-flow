namespace CodeFlow.Git;

/// <summary>A configured remote and the URL it fetches from.</summary>
public sealed record RemoteInfo(string Name, string Url);

/// <summary>
/// Remote listing and URL rewriting (GIT-026).
/// </summary>
public static class Remotes
{
    /// <summary>Every configured remote. A remote with no URL reports <c>""</c>, never a missing field.</summary>
    public static IReadOnlyList<RemoteInfo> List(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);
        return repo.Network.Remotes
            .Select(remote => new RemoteInfo(remote.Name, remote.Url ?? string.Empty))
            .ToList();
    }

    /// <summary>
    /// Points a remote's fetch <b>and</b> push URL at the same value.
    /// </summary>
    /// <remarks>
    /// Both, always: 1.7.2 writes <c>url</c> and then overwrites <c>pushurl</c> with the
    /// same string, so a repository that deliberately fetched and pushed to different URLs loses
    /// that distinction. Preserved as-is — this surface cannot express the split.
    /// </remarks>
    public static void SetUrl(string repoPath, string name, string url)
    {
        using var repo = RepoStatus.Open(repoPath);
        repo.Network.Remotes.Update(name, r => r.Url = url, r => r.PushUrl = url);
    }
}
