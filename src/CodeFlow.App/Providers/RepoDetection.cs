namespace CodeFlow.Providers;

/// <summary>A GitHub repository recognised from a git remote.</summary>
/// <param name="Host">
/// <c>github.com</c> or an Enterprise hostname, normalised to the matching allow-list entry so the
/// token lookup and the API base URL both target the right server.
/// </param>
public sealed record DetectedGitHubRepo(string Host, string Owner, string Repo);

/// <summary>An Azure DevOps repository recognised from a git remote.</summary>
public sealed record DetectedAzureRepo(string Org, string Project, string Repo);

/// <summary>
/// Recognises a repository from its git remote URL, from each provider's <c>detect_from_remote_url</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pure parsing, no network. The Azure half is here even though its REST client is a later slice:
/// auto-linking has to be able to say "this is an Azure repo and its token is missing" rather than
/// "nothing recognised", and that answer needs only the grammar.
/// </para>
/// <para>
/// The two halves share no code, and that is not an oversight — they disagree on nearly everything.
/// GitHub accepts any scheme <em>and</em> the scp-like SSH form but gates on a host allow-list; Azure
/// requires <c>http(s)</c> or one exact SSH prefix, needs no allow-list, and matches its path shapes
/// <em>exactly</em> rather than tolerating extra segments. They also strip <c>.git</c> differently and
/// decode escapes differently. Sharing a helper between them would quietly average those apart.
/// </para>
/// </remarks>
public static class RepoDetection
{
    /// <summary>The canonical public host. Everything else is treated as a GitHub Enterprise Server.</summary>
    public const string GitHubCom = "github.com";

    private const string AzureSshPrefix = "git@ssh.dev.azure.com:v3/";

    /// <summary>
    /// Recognises a GitHub remote, but <b>only</b> when its host is a known GitHub host.
    /// </summary>
    /// <remarks>
    /// Without the allow-list a GitLab, Bitbucket or self-hosted remote would be indistinguishable
    /// from a GitHub Enterprise one, so an unknown host returns <see langword="null"/> and the user
    /// falls back to linking by hand.
    /// </remarks>
    public static DetectedGitHubRepo? GitHub(string remoteUrl, IReadOnlyList<string> knownHosts)
    {
        if (SplitHostPath(remoteUrl) is not { } parts)
        {
            return null;
        }

        var (host, path) = parts;

        if (PrLink.MatchHost(knownHosts, host) is not { } matched)
        {
            return null;
        }

        // Exactly the first two segments; a deeper path is not a plain clone URL, and the extra
        // segments are dropped rather than making the whole remote unrecognised.
        var segments = path.Split('/').Where(segment => segment.Length > 0).ToArray();

        return segments.Length >= 2
            ? new DetectedGitHubRepo(matched, segments[0], TrimAllGitSuffixes(segments[1]))
            : null;
    }

    /// <summary>
    /// Recognises an Azure DevOps remote in the three shapes git actually stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HTTPS via <c>dev.azure.com/{org}/{project}/_git/{repo}</c>; the legacy
    /// <c>{org}.visualstudio.com/[DefaultCollection/]{project}/_git/{repo}</c>; and the SSH form
    /// <c>git@ssh.dev.azure.com:v3/{org}/{project}/{repo}</c>.
    /// </para>
    /// <para>
    /// Each shape is matched on an <em>exact</em> segment count, unlike the GitHub path — a URL with
    /// anything extra is not recognised at all rather than being truncated to fit. And the SSH form is
    /// one literal prefix, not a host pattern, so <c>vs-ssh.visualstudio.com</c> is deliberately not
    /// recognised.
    /// </para>
    /// </remarks>
    public static DetectedAzureRepo? Azure(string remoteUrl)
    {
        // Applied to the whole URL, and repeatedly, before anything is split off it.
        var url = TrimAllGitSuffixes(remoteUrl.Trim());

        if (url.StartsWith(AzureSshPrefix, StringComparison.Ordinal))
        {
            var ssh = url[AzureSshPrefix.Length..].Split('/').Where(s => s.Length > 0).ToArray();

            return ssh.Length == 3
                ? new DetectedAzureRepo(DecodeSpaces(ssh[0]), DecodeSpaces(ssh[1]), DecodeSpaces(ssh[2]))
                : null;
        }

        // No scheme means no Azure remote — unlike GitHub, the scp-like form is not accepted here
        // beyond the one prefix above.
        var withoutScheme = url.StartsWith("https://", StringComparison.Ordinal) ? url[8..]
            : url.StartsWith("http://", StringComparison.Ordinal) ? url[7..]
            : null;

        if (withoutScheme is null)
        {
            return null;
        }

        var withoutUserInfo = StripUserInfo(withoutScheme);
        var slash = withoutUserInfo.IndexOf('/', StringComparison.Ordinal);
        var host = slash < 0 ? withoutUserInfo : withoutUserInfo[..slash];
        var pathParts = (slash < 0 ? string.Empty : withoutUserInfo[(slash + 1)..])
            .Split('/')
            .Where(segment => segment.Length > 0)
            .ToArray();

        if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return pathParts is [var org, var project, "_git", var repo]
                ? new DetectedAzureRepo(DecodeSpaces(org), DecodeSpaces(project), DecodeSpaces(repo))
                : null;
        }

        // Case-sensitive, as in 1.7.2, and the organisation keeps the host's own casing
        // rather than being folded — it becomes part of the keychain key.
        if (!host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            return null;
        }

        var legacyOrg = host[..^".visualstudio.com".Length];
        var rest = pathParts.AsSpan();
        if (rest.Length > 0 && rest[0] == "DefaultCollection")
        {
            rest = rest[1..];
        }

        return rest is [var legacyProject, "_git", var legacyRepo]
            ? new DetectedAzureRepo(legacyOrg, DecodeSpaces(legacyProject), DecodeSpaces(legacyRepo))
            : null;
    }

    /// <summary>
    /// Splits a git remote URL into its host and its path, for the GitHub grammar.
    /// </summary>
    /// <remarks>
    /// Covers scheme URLs (<c>https://host/owner/repo</c>, <c>ssh://git@host/owner/repo</c>,
    /// optionally with embedded credentials) and the scp-like SSH form (<c>git@host:owner/repo</c>).
    /// A trailing slash goes first, then <b>one</b> <c>.git</c> — the segment-level trim later removes
    /// any others, which is 1.7.2's own split of responsibilities.
    /// </remarks>
    private static (string Host, string Path)? SplitHostPath(string remoteUrl)
    {
        var url = remoteUrl.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.Ordinal))
        {
            url = url[..^4];
        }

        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var after = StripUserInfo(url[(scheme + 3)..]);
            var slash = after.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? null : (after[..slash], after[(slash + 1)..]);
        }

        var scp = StripUserInfo(url);
        var colon = scp.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? null : (scp[..colon], scp[(colon + 1)..]);
    }

    /// <summary>Drops everything up to and including the last <c>@</c>, which is the user info.</summary>
    private static string StripUserInfo(string value) =>
        value.Contains('@', StringComparison.Ordinal) ? value[(value.LastIndexOf('@') + 1)..] : value;

    /// <summary>
    /// Removes <em>every</em> trailing <c>.git</c>, not just one.
    /// </summary>
    /// <remarks>
    /// The suffix is stripped repeatedly, so <c>repo.git.git</c> becomes <c>repo</c>. Silly input,
    /// but the loop costs nothing and stopping after one pass costs a mismatch nobody would look
    /// for.
    /// </remarks>
    private static string TrimAllGitSuffixes(string value)
    {
        while (value.EndsWith(".git", StringComparison.Ordinal))
        {
            value = value[..^4];
        }

        return value;
    }

    /// <summary>
    /// Azure's own path "decoder": <c>%20</c> to a space, and nothing else.
    /// </summary>
    /// <remarks>
    /// Not a percent-decoder — <c>%2F</c>, <c>%C3%A9</c> and friends survive verbatim. That is what
    /// 1.7.2 does (<c>decode_path_segment</c> is a single <c>replace</c>), and it is a
    /// different function from the full decoder <see cref="PrLink"/> applies to a browser link.
    /// </remarks>
    private static string DecodeSpaces(string segment) => segment.Replace("%20", " ", StringComparison.Ordinal);
}
