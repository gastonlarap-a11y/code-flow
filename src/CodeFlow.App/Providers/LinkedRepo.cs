using CodeFlow.Workspaces;

namespace CodeFlow.Providers;

/// <summary>
/// Which pull-request host a project is linked to's <c>enum LinkedRepo</c>.
/// </summary>
/// <remarks>
/// <para>
/// The single dispatch point every provider-aware command branches on, which is what keeps the
/// renderer, the PR store and the whole review pipeline provider-neutral.
/// </para>
/// <para>
/// This answers <em>which</em> host, and <see cref="IPullRequestHost"/> is what you get once the
/// credential is loaded too. The split is deliberate: two commands — auto-linking and the repository's
/// web address — need the host's identity without ever calling it, and one of them runs on a project
/// with no credential at all.
/// </para>
/// </remarks>
public abstract record LinkedRepo
{
    private LinkedRepo()
    {
    }

    /// <param name="Host"><c>github.com</c> or an Enterprise hostname — picks both the token and the API base.</param>
    public sealed record GitHub(string Host, string Owner, string Repo) : LinkedRepo;

    /// <param name="RepoId">
    /// A repository <em>name</em> in practice, despite the column being called <c>ado_repo_id</c>:
    /// Azure's Git REST API accepts a name anywhere it accepts a GUID, and the link flows store the
    /// canonical name so a notification e-mail's GUID never leaks into the UI.
    /// </param>
    public sealed record Azure(string Org, string Project, string RepoId) : LinkedRepo;

    /// <summary>
    /// Resolves which host a project talks to.
    /// </summary>
    /// <remarks>
    /// <b>GitHub wins.</b> If both link column sets are populated the project dispatches to GitHub, and
    /// that precedence is load-bearing rather than incidental: the link flows rely on it, and it is why
    /// re-linking clears both sets before writing one.
    /// </remarks>
    /// <exception cref="ProviderException">The project is linked to no host at all.</exception>
    public static LinkedRepo Resolve(Project project)
    {
        if (project.GithubOwner is { } owner && project.GithubRepo is { } repo)
        {
            return new GitHub(project.GithubHost ?? RepoDetection.GitHubCom, owner, repo);
        }

        if (project.AdoOrg is { } org && project.AdoProject is { } adoProject && project.AdoRepoId is { } repoId)
        {
            return new Azure(org, adoProject, repoId);
        }

        throw new ProviderException("This project isn't linked to a pull-request host yet");
    }
}

/// <summary>A provider operation that failed in a way the user should see.</summary>
/// <remarks>
/// The message crosses the IPC boundary verbatim as the JSON-RPC error, so these strings are a
/// contract with the renderer rather than diagnostics.
/// </remarks>
public sealed class ProviderException(string message) : Exception(message);
