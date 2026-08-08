using System.Text;

namespace CodeFlow.Providers;

/// <summary>Where a pasted pull-request link points.</summary>
/// <remarks>
/// The Azure case is parsed here even though the Azure REST client is a later slice: this is pure
/// URL grammar, the extracted test vectors cover both providers, and refusing to parse an Azure link
/// would report it as unrecognised rather than as a host that is not connected yet.
/// </remarks>
public abstract record PrLinkTarget
{
    private PrLinkTarget()
    {
    }

    /// <param name="Host"><c>github.com</c> or an Enterprise hostname — picks both the token and the API base.</param>
    public sealed record GitHub(string Host, string Owner, string Repo, long Number) : PrLinkTarget;

    public sealed record Azure(string Org, string Project, string Repo, long Number) : PrLinkTarget;
}

/// <summary>
/// Turns a pull-request <em>web</em> URL — the thing people paste into a chat — into the coordinates
/// the provider clients already take.
/// </summary>
/// <remarks>
/// Deliberately the mirror image of <see cref="RepoDetection"/>, and deliberately sharing no code
/// with it: that one reads a <em>git remote</em>, this reads the <em>browser link</em>. Nothing here
/// touches a network — resolving the link to a real PR and to a local repository is the command
/// layer's job.
/// </remarks>
public static class PrLink
{
    /// <summary>
    /// Parses a pasted pull-request link, or returns <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Null for anything that is not a PR URL on a host we can talk to — including a GitHub
    /// Enterprise host the user has not connected, which is indistinguishable from any other
    /// self-hosted git server.
    /// </remarks>
    public static PrLinkTarget? Parse(string url, IReadOnlyList<string> knownGitHubHosts)
    {
        if (Split(url) is not { } parts)
        {
            return null;
        }

        var (host, segments) = parts;

        // GitHub first, then Azure — 1.7.2's order, and it matters: a host on the GitHub
        // allow-list is never re-examined as an Azure one.
        return ParseGitHub(host, segments, knownGitHubHosts)
            ?? (PrLinkTarget?)ParseAzure(host, segments);
    }

    /// <summary>
    /// Splits a pasted link into its host and its decoded path segments.
    /// </summary>
    /// <remarks>
    /// Tolerates everything a real copy/paste carries: a missing scheme, a <c>?_a=files</c> query, a
    /// <c>#discussion_r…</c> fragment, a trailing slash, embedded user info.
    /// </remarks>
    private static (string Host, string[] Segments)? Split(string url)
    {
        var cleaned = url.Trim().Split('#')[0].Split('?')[0].TrimEnd('/');

        var withoutScheme = cleaned.StartsWith("https://", StringComparison.Ordinal) ? cleaned[8..]
            : cleaned.StartsWith("http://", StringComparison.Ordinal) ? cleaned[7..]
            : cleaned;

        var withoutUserInfo = withoutScheme.Contains('@', StringComparison.Ordinal)
            ? withoutScheme[(withoutScheme.LastIndexOf('@') + 1)..]
            : withoutScheme;

        var slash = withoutUserInfo.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            // No path at all, or an empty host.
            return null;
        }

        var segments = withoutUserInfo[(slash + 1)..]
            .Split('/')
            .Where(segment => segment.Length > 0)
            .Select(PercentDecode)
            .ToArray();

        return (withoutUserInfo[..slash], segments);
    }

    /// <summary>
    /// Recognises <c>https://{host}/{owner}/{repo}/pull/{n}</c> plus whatever tab follows it.
    /// </summary>
    /// <remarks>
    /// As with a git remote, the host must be one we <em>know</em> is GitHub: an arbitrary
    /// self-hosted host with the same path shape could be anything. The host is normalised to the
    /// matching allow-list entry so the token key stays consistent with what was saved.
    /// </remarks>
    private static PrLinkTarget.GitHub? ParseGitHub(string host, string[] segments, IReadOnlyList<string> knownHosts)
    {
        if (MatchHost(knownHosts, host) is not { } matched || segments.Length < 4)
        {
            return null;
        }

        var kind = segments[2];
        if (!kind.Equals("pull", StringComparison.OrdinalIgnoreCase)
            && !kind.Equals("pulls", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return long.TryParse(segments[3], out var number)
            ? new PrLinkTarget.GitHub(matched, segments[0], TrimGitSuffix(segments[1]), number)
            : null;
    }

    /// <summary>
    /// Recognises the Azure DevOps shapes the portal actually produces.
    /// </summary>
    /// <remarks>
    /// <c>dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{n}</c>, the same without the project
    /// segment (Azure omits it when the project and repo share a name), and the legacy
    /// <c>{org}.visualstudio.com</c> host with an optional <c>/DefaultCollection</c> prefix.
    /// </remarks>
    private static PrLinkTarget.Azure? ParseAzure(string host, string[] segments)
    {
        var lower = host.ToLowerInvariant();

        string org;
        ReadOnlySpan<string> rest;
        if (lower == "dev.azure.com")
        {
            if (segments.Length == 0)
            {
                return null;
            }

            org = segments[0];
            rest = segments.AsSpan(1);
        }
        else if (lower.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            org = lower[..^".visualstudio.com".Length];
            rest = segments;
        }
        else
        {
            return null;
        }

        if (rest.Length > 0 && rest[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[1..];
        }

        // With a project segment, then without it — Azure drops it when it matches the repo name.
        if (rest.Length >= 5 && IsGit(rest[1]) && IsPullRequest(rest[3]) && long.TryParse(rest[4], out var number))
        {
            return new PrLinkTarget.Azure(org, rest[0], rest[2], number);
        }

        if (rest.Length >= 4 && IsGit(rest[0]) && IsPullRequest(rest[2]) && long.TryParse(rest[3], out var bare))
        {
            return new PrLinkTarget.Azure(org, rest[1], rest[1], bare);
        }

        return null;
    }

    private static bool IsGit(string segment) => segment.Equals("_git", StringComparison.OrdinalIgnoreCase);

    private static bool IsPullRequest(string segment) =>
        segment.Equals("pullrequest", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("pullrequests", StringComparison.OrdinalIgnoreCase);

    /// <summary>The allow-list entry matching <paramref name="host"/>, so the host is normalised to it.</summary>
    /// <remarks>
    /// Returning the stored spelling rather than the pasted one is what keeps the keychain key
    /// consistent with what was saved when the user connected the host.
    /// </remarks>
    internal static string? MatchHost(IReadOnlyList<string> knownHosts, string host) =>
        knownHosts.FirstOrDefault(known => known.Equals(host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes every trailing <c>.git</c>, not just one — see <c>RepoDetection</c>.</summary>
    private static string TrimGitSuffix(string repo)
    {
        while (repo.EndsWith(".git", StringComparison.Ordinal))
        {
            repo = repo[..^4];
        }

        return repo;
    }

    /// <summary>
    /// Decodes the <c>%XX</c> escapes a browser puts in the path.
    /// </summary>
    /// <remarks>
    /// Azure DevOps org, project and repo names routinely contain spaces ("Marketing Website" →
    /// <c>Marketing%20Website</c>) and the REST clients re-encode them themselves, so what they are
    /// handed has to be the decoded name. A stray <c>%</c> that is not a valid escape is left as-is
    /// rather than dropped. Decoded bytes are UTF-8 with replacement, matching
    /// lossy UTF-8 decoding, which substitutes rather than throwing.
    /// </remarks>
    private static string PercentDecode(string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        // Latin-1 round-trips each char to one byte, which is what the escapes and the untouched
        // ASCII both need; the result is then read back as UTF-8.
        var bytes = new List<byte>(value.Length);

        for (var i = 0; i < value.Length;)
        {
            // The bound is 1.7.2's, index-for-index: i + 2 must be a real index, so a
            // complete escape at the very end still decodes and a truncated "%4" does not.
            if (value[i] == '%' && i + 2 < value.Length
                && HexValue(value[i + 1]) is { } high && HexValue(value[i + 2]) is { } low)
            {
                bytes.Add((byte)((high * 16) + low));
                i += 3;
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            i++;
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static int? HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => null,
    };
}
