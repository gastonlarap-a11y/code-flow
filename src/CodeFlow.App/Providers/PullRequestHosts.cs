using CodeFlow.Providers.Azure;
using CodeFlow.Providers.GitHub;
using CodeFlow.Security;

namespace CodeFlow.Providers;

/// <summary>
/// Where a project or a pasted link becomes coordinates plus a credential — one host, ready to talk to.
/// </summary>
/// <remarks>
/// <para>
/// Extracted so the pull-request panel's commands and the review pipeline's resolve a host the same
/// way. They are separate features and separate registration groups, but "which host, with which
/// token" is one question, and two copies of the answer would drift the first time one of them
/// gained a provider.
/// </para>
/// <para>
/// Resolving the link and loading the credential are deliberately one step: they can only fail
/// together, in ways the user needs told apart — "this project isn't linked" versus "its token is
/// missing" — and both messages cross the IPC boundary verbatim.
/// </para>
/// </remarks>
internal static class PullRequestHosts
{
    /// <summary>The host a linked project's pull requests live on.</summary>
    public static IPullRequestHost For(HttpClient http, LinkedRepo link) => link switch
    {
        LinkedRepo.GitHub github => new GitHubHost(
            http, github.Host, github.Owner, github.Repo, GitHubToken(github.Host)),

        LinkedRepo.Azure azure => new AzureHost(
            http, azure.Org, azure.Project, azure.RepoId, PatForOrg(azure.Org)),

        // LinkedRepo is a closed hierarchy with a private constructor, so this is unreachable; the
        // compiler cannot prove it, and a silent default would be worse than saying so.
        _ => throw new ProviderException("This project isn't linked to a pull-request host yet"),
    };

    /// <summary>The host a pasted pull-request link points at, and the number it names.</summary>
    public static (IPullRequestHost Host, long Number) For(HttpClient http, PrLinkTarget target) => target switch
    {
        PrLinkTarget.GitHub github => (
            new GitHubHost(http, github.Host, github.Owner, github.Repo, GitHubToken(github.Host)),
            github.Number),

        PrLinkTarget.Azure azure => (
            new AzureHost(http, azure.Org, azure.Project, azure.Repo, PatForOrg(azure.Org)),
            azure.Number),

        _ => throw new ProviderException("That isn't a pull-request link CodeFlow can read"),
    };

    /// <summary>
    /// The saved Azure DevOps PAT for an organisation.
    /// </summary>
    /// <remarks>
    /// Keyed per organisation, where GitHub's is keyed per host: one PAT authenticates against one
    /// organisation. The message names the organisation and points at Settings, which is where the
    /// renderer's "connect it" link goes.
    /// </remarks>
    public static string PatForOrg(string org) =>
        CredentialStore.Get(CredentialStore.AdoPatKey(org))
        ?? throw new ProviderException(
            $"No Azure DevOps token saved for organization \"{org}\" — connect it in Settings first");

    /// <summary>
    /// The saved GitHub token for a host.
    /// </summary>
    /// <remarks>
    /// A different message from <c>github_authenticated_user</c>'s for the same condition, because the
    /// reference has two. This one names the host and points at Settings.
    /// </remarks>
    public static string GitHubToken(string host) =>
        CredentialStore.Get(CredentialStore.GitHubTokenKey(host))
        ?? throw new ProviderException($"No GitHub token saved for \"{host}\" — connect it in Settings first");
}
