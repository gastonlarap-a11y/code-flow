using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Activity;
using CodeFlow.Ai;
using CodeFlow.Git;
using CodeFlow.Ipc;
using CodeFlow.Providers.Azure;
using CodeFlow.Providers.GitHub;
using CodeFlow.Security;
using CodeFlow.Storage;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Providers;

/// <summary>
/// Linking a project to a pull-request host, and everything the PR panel reads or does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both providers are live.</b> Every command that depends on the host resolves an
/// <see cref="IPullRequestHost"/> once — which picks the coordinates and the credential together — and
/// then says what it wants. Slice 6 shipped this with the Azure arm refusing, because an interface with
/// one real implementation is what <c>.claude/rules/dotnet.md</c> rejects; slice 7 made the second one real.
/// </para>
/// <para>
/// Three commands do <em>not</em> dispatch, because they are Azure's alone: the two lookups behind the
/// manual link dialog's cascading dropdowns, and the manual link itself. There is no GitHub equivalent —
/// a GitHub repository is named by owner and repo, which the user can simply type.
/// </para>
/// <para>
/// <c>open_external_url</c> is absent by design, and <c>open_repo_in_browser</c> is
/// <see href="#">repo_web_url</see> here: opening a browser belongs to the process that owns the window,
/// and the shell already exposes it with the same http(s) gate. The renderer's two wrappers keep their
/// exported signatures — the same treatment slice 3 gave <c>quit_app</c> and <c>pick_folder</c>.
/// </para>
/// </remarks>
public static class ProviderCommands
{
    public static CommandRegistry AddProviderCommands(
        this CommandRegistry registry, Database database, AiRunRegistry runs, HttpClient http) =>
        registry
            // ---------- linking ----------
            .Add("link_project_github", (p, ct) =>
            {
                var id = Arg(p, "id");
                var owner = Arg(p, "githubOwner");
                var repo = Arg(p, "githubRepo");
                var host = Arg(p, "githubHost");
                return Unit(database, c => ProjectStore.LinkGithub(c, id, owner, repo, host), ct);
            })
            .Add("link_project_ado", (p, ct) =>
            {
                var id = Arg(p, "id");
                var org = Arg(p, "adoOrg");
                var project = Arg(p, "adoProject");
                var repoId = Arg(p, "adoRepoId");
                return Unit(database, c => ProjectStore.LinkAdo(c, id, org, project, repoId), ct);
            })
            .Add("unlink_project", (p, ct) =>
            {
                var id = Arg(p, "id");
                return Unit(database, c => ProjectStore.Unlink(c, id), ct);
            })
            // ---------- the manual Azure link dialog's two lookups ----------
            .Add("ado_list_projects", async (p, ct) =>
            {
                var org = Arg(p, "org");
                var projects = await AzureClient.ListProjectsAsync(http, org, PullRequestHosts.PatForOrg(org), ct)
                    .ConfigureAwait(false);

                return Json(projects, ProviderJsonContext.Default.IReadOnlyListAdoProject);
            })
            .Add("ado_list_repos", async (p, ct) =>
            {
                var org = Arg(p, "org");
                var repos = await AzureClient.ListReposAsync(http, org, Arg(p, "project"), PullRequestHosts.PatForOrg(org), ct)
                    .ConfigureAwait(false);

                return Json(repos, ProviderJsonContext.Default.IReadOnlyListAdoRepo);
            })
            .Add("github_authenticated_user", async (p, ct) =>
            {
                var host = Arg(p, "host");

                // A different message from the one every other command uses for the same condition.
                // CodeFlow 1.7.2 has both; reproduced rather than unified.
                var token = CredentialStore.Get(CredentialStore.GitHubTokenKey(host))
                    ?? throw new ProviderException("No GitHub token saved for this host");

                var login = await GitHubClient.GetAuthenticatedUserAsync(http, host, token, ct).ConfigureAwait(false);
                return Json(login, ProviderJsonContext.Default.String);
            })
            .Add("auto_link_project", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                return Write(database, c => AutoLink(c, projectId), ProviderJsonContext.Default.AutoLinkResult, ct);
            })
            .Add("repo_web_url", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                return Write(database, c => RepoWebUrl(c, projectId), ProviderJsonContext.Default.String, ct);
            })
            // ---------- a project's pull requests ----------
            .Add("list_pull_requests", async (p, ct) =>
            {
                var host = await HostForProjectAsync(database, http, Arg(p, "projectId"), ct).ConfigureAwait(false);

                IReadOnlyList<PullRequestSummary> pulls;
                try
                {
                    pulls = await host.ListPullRequestsAsync(ct).ConfigureAwait(false);
                }
                catch (AzureException refused) when (refused.Unauthorized)
                {
                    // DIVERGENCE-PROV-b, XLANG-012. The sidebar's only channel is the error string, and
                    // it has to tell "the token was refused" from "the host was unreachable" — one is
                    // fixed by replacing a credential, the other by retrying. The prefix goes on here
                    // rather than at the throw site so it reaches this one consumer and no other.
                    throw new AzureException(AzureException.RefusedPrefix + refused.Message, unauthorized: true);
                }

                return Json(pulls, ProviderJsonContext.Default.IReadOnlyListPullRequestSummary);
            })
            .Add("list_pr_comment_threads", async (p, ct) =>
            {
                var host = await HostForProjectAsync(database, http, Arg(p, "projectId"), ct).ConfigureAwait(false);
                var threads = await host.ListCommentThreadsAsync(Number(p, "prId"), ct).ConfigureAwait(false);

                return Json(threads, ProviderJsonContext.Default.IReadOnlyListPrCommentThread);
            })
            .Add("pr_review_decision", async (p, ct) =>
            {
                var host = await HostForProjectAsync(database, http, Arg(p, "projectId"), ct).ConfigureAwait(false);
                var decision = await host.ViewerDecisionAsync(Number(p, "prId"), ct).ConfigureAwait(false);

                return Json(decision, ProviderJsonContext.Default.String);
            })
            .Add("create_pull_request", async (p, ct) =>
            {
                var host = await HostForProjectAsync(database, http, Arg(p, "projectId"), ct).ConfigureAwait(false);
                var created = await host.CreatePullRequestAsync(
                    Arg(p, "title"), Arg(p, "description"), Arg(p, "sourceBranch"), Arg(p, "targetBranch"),
                    Bool(p, "draft"), ct).ConfigureAwait(false);

                return Json(created, ProviderJsonContext.Default.PullRequestSummary);
            })
            .Add("act_on_pull_request", async (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var prId = Number(p, "prId");
                var action = Arg(p, "action");

                var host = await HostForProjectAsync(database, http, projectId, ct).ConfigureAwait(false);

                try
                {
                    await host.ActOnAsync(prId, action, Optional(p, "body") ?? string.Empty, ct).ConfigureAwait(false);
                }
                catch (GitHubException refused) when (refused.SelfApproval)
                {
                    // DIVERGENCE-PROV-c, XLANG-013. Same device and same reasoning as the
                    // CREDENTIAL_REFUSED prefix above: the toast's only channel is the error string,
                    // and this one failure needs a different sentence from every other 422 — there is
                    // no retry and no credential that fixes it, because it is GitHub's rule.
                    throw new GitHubException(
                        GitHubException.SelfApprovalPrefix + refused.Message, selfApproval: true);
                }

                // Re-read from the host rather than patching the copy in hand: the action may have
                // changed more than the field it named.
                var pr = await host.GetPullRequestAsync(prId, ct).ConfigureAwait(false);

                var activity = await database.WriteAsync(
                    c => JobHistoryStore.Add(
                        c,
                        Guid.NewGuid().ToString(),
                        projectId,
                        "pr-action",
                        // Not a translated string: 1.7.2 builds "#42 Fix login bug" and the
                        // activity list renders it as-is.
                        string.Create(CultureInfo.InvariantCulture, $"#{pr.Id} {pr.Title}"),
                        "done",
                        pr.Url,
                        null,
                        ActionMeta(pr, action)),
                    ct).ConfigureAwait(false);

                return Json(new PrActionOutcome(pr, activity), ProviderJsonContext.Default.PrActionOutcome);
            })
            .Add("generate_pr_description", async (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var sourceBranch = Arg(p, "sourceBranch");
                var targetBranch = Arg(p, "targetBranch");

                var (project, config, template) = await database.ReadAsync(
                    c =>
                    {
                        var found = ProjectStore.Get(c, projectId)
                            ?? throw new ProviderException("Project not found");

                        return (found,
                            AiRouting.Resolve(c, "pr_description"),
                            Settings.GetWorkspacePrompt(c, found.WorkspaceId, "pr_description"));
                    },
                    ct).ConfigureAwait(false);

                // base = target, head = source. The order reads backwards and matters: this is what a
                // merge of the source into the target would bring in.
                var diff = await Task.Run(
                    () => Diff.RenderForPrompt(Diff.BranchDiff(project.LocalPath, targetBranch, sourceBranch)), ct)
                    .ConfigureAwait(false);

                var raw = await AiOperations.GeneratePrDescriptionAsync(
                    AiEngineRunner.Bind(runs, http), config, sourceBranch, targetBranch, diff, template,
                    Run(p), ct).ConfigureAwait(false);

                return Json(ParseDraft(raw), ProviderJsonContext.Default.PrDescriptionDraft);
            })
            // ---------- a pull request reached by its link alone ----------
            .Add("resolve_pr_link", async (p, ct) =>
            {
                var resolution = await ResolveLinkAsync(database, http, Arg(p, "url"), ct).ConfigureAwait(false);
                return Json(resolution, ProviderJsonContext.Default.PrLinkResolution);
            })
            .Add("pr_link_pull_request", async (p, ct) =>
            {
                var (host, number) = await HostForLinkAsync(database, http, Arg(p, "url"), ct).ConfigureAwait(false);
                var pr = await host.GetPullRequestAsync(number, ct).ConfigureAwait(false);

                return Json(pr, ProviderJsonContext.Default.PullRequestSummary);
            })
            .Add("pr_link_comment_threads", async (p, ct) =>
            {
                var (host, number) = await HostForLinkAsync(database, http, Arg(p, "url"), ct).ConfigureAwait(false);
                var threads = await host.ListCommentThreadsAsync(number, ct).ConfigureAwait(false);

                return Json(threads, ProviderJsonContext.Default.IReadOnlyListPrCommentThread);
            })
            .Add("pr_link_decision", async (p, ct) =>
            {
                var (host, number) = await HostForLinkAsync(database, http, Arg(p, "url"), ct).ConfigureAwait(false);
                var decision = await host.ViewerDecisionAsync(number, ct).ConfigureAwait(false);

                return Json(decision, ProviderJsonContext.Default.String);
            })
            .Add("act_on_pr_link", async (p, ct) =>
            {
                var (host, number) = await HostForLinkAsync(database, http, Arg(p, "url"), ct).ConfigureAwait(false);

                try
                {
                    await host.ActOnAsync(number, Arg(p, "action"), Optional(p, "body") ?? string.Empty, ct)
                        .ConfigureAwait(false);
                }
                catch (GitHubException refused) when (refused.SelfApproval)
                {
                    // The other half of DIVERGENCE-PROV-c. A pasted link reaches the same GitHub rule
                    // by a different command, and a user who met the friendly message on a linked
                    // project would be confused to meet raw JSON here.
                    throw new GitHubException(
                        GitHubException.SelfApprovalPrefix + refused.Message, selfApproval: true);
                }

                // No history row here, unlike act_on_pull_request: job_history belongs to a project and
                // this pull request has none. The caller files the decision in the session's own activity.
                var pr = await host.GetPullRequestAsync(number, ct).ConfigureAwait(false);

                return Json(pr, ProviderJsonContext.Default.PullRequestSummary);
            });

    // ---------- linking ----------

    /// <summary>
    /// Binds a project to whichever host its remotes point at, if a credential for it exists.
    /// </summary>
    /// <remarks>
    /// Scans <em>every</em> remote, not just <c>origin</c> — a repository whose PR host lives on a
    /// differently-named remote should still bind on its own — with <c>origin</c> checked first as the
    /// canonical upstream. It binds to the first host it recognises <b>and already has a token for</b>,
    /// so with both providers connected each repository links to the one that is actually its own with no
    /// "pick one" step. A host recognised without a credential is remembered and reported only if
    /// nothing turned out to be linkable outright.
    /// </remarks>
    private static AutoLinkResult AutoLink(SqliteConnection connection, string projectId)
    {
        var project = ProjectStore.Get(connection, projectId)
            ?? throw new ProviderException("Project not found");

        if (IsLinked(project))
        {
            return new AutoLinkResult.Linked(project);
        }

        var knownHosts = KnownHosts.ForGitHub(connection);
        AutoLinkResult.NeedsToken? needsToken = null;

        foreach (var remote in OrderedRemotes(project.LocalPath))
        {
            if (RepoDetection.GitHub(remote.Url, knownHosts) is { } github)
            {
                if (CredentialStore.Has(CredentialStore.GitHubTokenKey(github.Host)))
                {
                    ProjectStore.LinkGithub(connection, projectId, github.Owner, github.Repo, github.Host);
                    return new AutoLinkResult.Linked(ProjectStore.Get(connection, projectId)!);
                }

                // The owner, not the host — see the note on AutoLinkResult.NeedsToken.
                needsToken ??= new AutoLinkResult.NeedsToken("github", github.Owner);
            }
            else if (RepoDetection.Azure(remote.Url) is { } azure)
            {
                if (CredentialStore.Has(CredentialStore.AdoPatKey(azure.Org)))
                {
                    ProjectStore.LinkAdo(connection, projectId, azure.Org, azure.Project, azure.Repo);
                    return new AutoLinkResult.Linked(ProjectStore.Get(connection, projectId)!);
                }

                needsToken ??= new AutoLinkResult.NeedsToken("azure", azure.Org);
            }
        }

        return needsToken ?? (AutoLinkResult)new AutoLinkResult.NotDetected();
    }

    /// <summary>
    /// The repository's page on its host, rebuilt from its git remotes.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately ignores the project's stored link columns.</b> They may hold an Azure repository
    /// GUID from the manual picker, or be briefly stale for a repository auto-linked this session, and
    /// neither makes a URL a human can open. The remotes are the source of truth here.
    /// </remarks>
    private static string RepoWebUrl(SqliteConnection connection, string projectId)
    {
        var project = ProjectStore.Get(connection, projectId)
            ?? throw new ProviderException("Project not found");

        var knownHosts = KnownHosts.ForGitHub(connection);

        foreach (var remote in OrderedRemotes(project.LocalPath))
        {
            if (RepoDetection.GitHub(remote.Url, knownHosts) is { } github)
            {
                return $"https://{github.Host}/{github.Owner}/{github.Repo}";
            }

            if (RepoDetection.Azure(remote.Url) is { } azure)
            {
                return "https://dev.azure.com/"
                    + $"{WebEncode(azure.Org)}/{WebEncode(azure.Project)}/_git/{WebEncode(azure.Repo)}";
            }
        }

        throw new ProviderException("Couldn't determine this repository's web address from its remote");
    }

    /// <summary>The project's remotes with <c>origin</c> first, or none if they cannot be read.</summary>
    /// <remarks>
    /// A repository whose folder has moved yields nothing rather than failing the command: the caller's
    /// answer becomes "not detected", which is both true and actionable.
    /// </remarks>
    private static IEnumerable<RemoteInfo> OrderedRemotes(string repoPath)
    {
        IReadOnlyList<RemoteInfo> remotes;
        try
        {
            remotes = Remotes.List(repoPath);
        }
        catch (Exception failure) when (failure is LibGit2Sharp.LibGit2SharpException or IOException
                                            or ArgumentException or UnauthorizedAccessException)
        {
            return [];
        }

        return remotes
            .Where(remote => remote.Name == "origin")
            .Concat(remotes.Where(remote => remote.Name != "origin"));
    }

    /// <summary>Whether a project already resolves to a host.</summary>
    private static bool IsLinked(Project project)
    {
        try
        {
            LinkedRepo.Resolve(project);
            return true;
        }
        catch (ProviderException)
        {
            return false;
        }
    }

    /// <summary>Azure's own path encoder: spaces, and nothing else.</summary>
    /// <remarks>
    /// Not a URL encoder. It is what 1.7.2 does, and a browser resolves the rest on its own —
    /// escaping more would change URLs that currently work.
    /// </remarks>
    private static string WebEncode(string segment) => segment.Replace(" ", "%20", StringComparison.Ordinal);

    // ---------- pasted links ----------

    /// <summary>
    /// Works out what a pasted link points at, and binds a local repository to it when one matches.
    /// </summary>
    private static async Task<PrLinkResolution> ResolveLinkAsync(
        Database database, HttpClient http, string url, CancellationToken cancellationToken)
    {
        var (target, projects) = await database.ReadAsync(
            c => (PrLink.Parse(url, KnownHosts.ForGitHub(c)), ProjectStore.All(c)),
            cancellationToken).ConfigureAwait(false);

        // Not an error: a link that is not a pull request — or is one on an Enterprise host the user has
        // not connected — is a state the UI shows, not a failure.
        return target switch
        {
            null => new PrLinkResolution.Unrecognized(),
            PrLinkTarget.GitHub github =>
                await ResolveGitHubLinkAsync(database, http, projects, github, cancellationToken)
                    .ConfigureAwait(false),
            PrLinkTarget.Azure azure =>
                await ResolveAzureLinkAsync(database, http, projects, azure, cancellationToken)
                    .ConfigureAwait(false),
            _ => new PrLinkResolution.Unrecognized(),
        };
    }

    private static async Task<PrLinkResolution> ResolveGitHubLinkAsync(
        Database database, HttpClient http, IReadOnlyList<Project> projects, PrLinkTarget.GitHub github,
        CancellationToken cancellationToken)
    {
        if (CredentialStore.Get(CredentialStore.GitHubTokenKey(github.Host)) is not { } token)
        {
            // The host here, where auto-linking reports the owner.
            return new PrLinkResolution.NeedsToken("github", github.Host);
        }

        var pr = await GitHubClient.GetPullRequestAsync(
            http, github.Host, github.Owner, github.Repo, github.Number, token, cancellationToken)
            .ConfigureAwait(false);

        var matched = await database.WriteAsync(
            c => FindProjectForLink(
                c,
                projects,
                project => Same(project.GithubOwner, github.Owner)
                           && Same(project.GithubRepo, github.Repo)
                           && Same(project.GithubHost ?? RepoDetection.GitHubCom, github.Host),
                remoteUrl => RepoDetection.GitHub(remoteUrl, KnownHosts.ForGitHub(c)) is { } detected
                             && Same(detected.Host, github.Host)
                             && Same(detected.Owner, github.Owner)
                             && Same(detected.Repo, github.Repo),
                (conn, id) => ProjectStore.LinkGithub(conn, id, github.Owner, github.Repo, github.Host)),
            cancellationToken).ConfigureAwait(false);

        return matched is { } project
            ? new PrLinkResolution.Ready(project.Id, project.WorkspaceId, project.Name, pr)
            : new PrLinkResolution.NoLocalRepo(
                "github",
                $"{github.Owner}/{github.Repo}",
                $"https://{github.Host}/{github.Owner}/{github.Repo}.git",
                pr);
    }

    /// <summary>
    /// The Azure half of link resolution, which has to learn the real names before it can match anything.
    /// </summary>
    /// <remarks>
    /// A link can address its project and repository by GUID — Azure's own notification e-mails do — and a
    /// GUID matches no git remote, because a remote only ever spells out names. So everything after the
    /// fetch uses the names Azure itself reported rather than what the link carried: that is what is
    /// matched against local clones, and what is written into the project's link columns.
    /// </remarks>
    private static async Task<PrLinkResolution> ResolveAzureLinkAsync(
        Database database, HttpClient http, IReadOnlyList<Project> projects, PrLinkTarget.Azure azure,
        CancellationToken cancellationToken)
    {
        if (CredentialStore.Get(CredentialStore.AdoPatKey(azure.Org)) is not { } pat)
        {
            // The organisation, which is what a PAT is keyed by — and what the "connect it" hint names.
            return new PrLinkResolution.NeedsToken("azure", azure.Org);
        }

        AzurePullRequest detail;
        try
        {
            detail = await AzureClient.GetPullRequestAsync(
                http, azure.Org, azure.Project, azure.Repo, azure.Number, pat, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AzureException refused) when (refused.Unauthorized)
        {
            // DIVERGENCE-PROV-b. A PAT is saved and Azure refused it, which is a different answer from
            // "no PAT saved" and needs a different sentence — the user has already done the thing
            // NeedsToken would tell them to do. Every other failure keeps travelling as an exception,
            // exactly as before.
            return new PrLinkResolution.Expired("azure", azure.Org);
        }

        var (pr, project, repo) = (detail.Summary, detail.ProjectName, detail.RepoName);

        var matched = await database.WriteAsync(
            c => FindProjectForLink(
                c,
                projects,
                candidate => Same(candidate.AdoOrg, azure.Org)
                             && Same(candidate.AdoProject, project)
                             && Same(candidate.AdoRepoId, repo)
                             // LinkedRepo.Resolve prefers GitHub, so a project carrying both column sets
                             // would dispatch there instead. Left to the remote pass, which repairs it.
                             && !(candidate.GithubOwner is not null && candidate.GithubRepo is not null),
                remoteUrl => RepoDetection.Azure(remoteUrl) is { } detected
                             && Same(detected.Org, azure.Org)
                             && Same(detected.Project, project)
                             && Same(detected.Repo, repo),
                // Azure's Git REST API takes the repository name wherever it takes a GUID, so the name off
                // the response is a valid ado_repo_id — and it is the one a human can read.
                (conn, id) => ProjectStore.LinkAdo(conn, id, azure.Org, project, repo)),
            cancellationToken).ConfigureAwait(false);

        return matched is { } local
            ? new PrLinkResolution.Ready(local.Id, local.WorkspaceId, local.Name, pr)
            : new PrLinkResolution.NoLocalRepo(
                "azure",
                // The project and the repository, not the organisation — Azure's own breadcrumb.
                $"{project}/{repo}",
                // No ".git" suffix, unlike GitHub's: Azure's clone URL is the repository's _git address.
                $"https://dev.azure.com/{WebEncode(azure.Org)}/{WebEncode(project)}/_git/{WebEncode(repo)}",
                pr);
    }

    /// <summary>
    /// Finds the local project a link belongs to, linking it if it was only matched by its remote.
    /// </summary>
    /// <remarks>
    /// Two passes, in this order. First the projects already carrying these coordinates, which needs no
    /// filesystem access and writes nothing. Only if that finds none does it read each project's remotes
    /// and, on the first match, <b>mutate the row</b>: unlink whatever host it was on — a project holds at
    /// most one, and a stale pair would misdispatch — then link this one, and re-read so the caller sees
    /// the new state. It stops at the first match rather than repairing every project that matches.
    /// </remarks>
    private static Project? FindProjectForLink(
        SqliteConnection connection,
        IReadOnlyList<Project> projects,
        Func<Project, bool> alreadyLinked,
        Func<string, bool> matchesRemote,
        Action<SqliteConnection, string> link)
    {
        if (projects.FirstOrDefault(alreadyLinked) is { } linked)
        {
            return linked;
        }

        foreach (var project in projects)
        {
            if (!OrderedRemotes(project.LocalPath).Any(remote => matchesRemote(remote.Url)))
            {
                continue;
            }

            ProjectStore.Unlink(connection, project.Id);
            link(connection, project.Id);

            return ProjectStore.Get(connection, project.Id);
        }

        return null;
    }

    /// <summary>
    /// The target a pasted link names, and the credential to reach it with.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ResolveLinkAsync"/> this fails outright on an unreadable link: by the time these
    /// commands run the user has already been through the resolution screen, so the typed states have no
    /// second audience.
    /// </remarks>
    private static async Task<(IPullRequestHost Host, long Number)> HostForLinkAsync(
        Database database, HttpClient http, string url, CancellationToken cancellationToken)
    {
        var target = await database.ReadAsync(c => PrLink.Parse(url, KnownHosts.ForGitHub(c)), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ProviderException("That isn't a pull-request link CodeFlow can read");

        return PullRequestHosts.For(http, target);
    }

    // ---------- shared plumbing ----------

    /// <summary>
    /// The host a project's pull requests live on, ready to talk to.
    /// </summary>
    /// <remarks>
    /// One place where a project becomes coordinates plus a credential, which is what lets every command
    /// above be a single sentence. Resolving the link and loading the credential are one step on purpose:
    /// they can only fail together in ways the user needs told apart — "this project isn't linked" versus
    /// "its token is missing".
    /// </remarks>
    private static async Task<IPullRequestHost> HostForProjectAsync(
        Database database, HttpClient http, string projectId, CancellationToken cancellationToken)
    {
        var link = await database.ReadAsync(
            c => LinkedRepo.Resolve(
                ProjectStore.Get(c, projectId) ?? throw new ProviderException("Project not found")),
            cancellationToken).ConfigureAwait(false);

        return PullRequestHosts.For(http, link);
    }

    /// <summary>The opaque JSON the activity row carries for a PR action.</summary>
    /// <remarks>
    /// camelCase keys, unlike everything else on the wire: 1.7.2 builds this object by hand
    /// rather than serialised from a record, so the wire naming policy never applied to it.
    /// </remarks>
    private static string ActionMeta(PullRequestSummary pr, string action) =>
        JsonSerializer.Serialize(
            new PrActionMeta(pr.Id, pr.Title, action), ProviderJsonContext.Default.PrActionMeta);

    /// <summary>
    /// Splits the model's draft into its title and body.
    /// </summary>
    /// <remarks>
    /// Scans for the first line whose leading whitespace-trimmed form starts with the literal
    /// <c>TITLE:</c> — <b>case-sensitively</b>, so <c>Title:</c> does not count. That line is dropped and
    /// every other line, before it as well as after, is kept in order. When no line matches at all the
    /// whole text becomes the body and the title is empty: the form is left for the user to fill rather
    /// than being handed a guess.
    /// </remarks>
    internal static PrDescriptionDraft ParseDraft(string raw)
    {
        var trimmed = raw.Trim();
        var title = string.Empty;
        var found = false;
        var body = new List<string>();

        foreach (var line in trimmed.Split('\n'))
        {
            if (!found && line.TrimStart() is var start && start.StartsWith("TITLE:", StringComparison.Ordinal))
            {
                title = start["TITLE:".Length..].Trim();
                found = true;
                continue;
            }

            body.Add(line);
        }

        return found
            ? new PrDescriptionDraft(title, string.Join('\n', body).Trim())
            : new PrDescriptionDraft(string.Empty, trimmed);
    }

    /// <summary>Case-insensitive comparison, as every host, owner and repo match in 1.7.2 is.</summary>
    private static bool Same(string? a, string b) => a is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // ---------- dispatch helpers ----------

    private static async ValueTask<ReadOnlyMemory<byte>> Write<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return Json(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> Unit(
        Database database, Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return "null"u8.ToArray();
    }

    private static ReadOnlyMemory<byte> Json<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToUtf8Bytes(value, type);

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static string? Optional(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static bool Bool(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>The run this command belongs to, for the one command that streams.</summary>
    private static AiRunContext? Run(JsonElement parameters) =>
        Optional(parameters, "runId") is { } runId && !string.IsNullOrWhiteSpace(runId)
            ? new AiRunContext(runId)
            : null;
}

/// <summary>The activity row's opaque metadata for a pull-request action.</summary>
/// <remarks>camelCase on purpose — see <see cref="ProviderCommands"/>'s note.</remarks>
internal sealed record PrActionMeta(
    [property: JsonPropertyName("prId")] long PrId,
    [property: JsonPropertyName("prTitle")] string PrTitle,
    [property: JsonPropertyName("action")] string Action);

/// <summary>Every type this feature puts on the wire.</summary>
/// <remarks>
/// snake_case for properties, because <c>renderer/src/types/domain.ts</c> declares these field
/// names. The discriminator <em>values</em> on the two tagged unions are unaffected by the policy
/// and stay PascalCase, which is what the renderer switches on.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(PullRequestSummary))]
[JsonSerializable(typeof(IReadOnlyList<PullRequestSummary>))]
[JsonSerializable(typeof(PrCommentThread))]
[JsonSerializable(typeof(IReadOnlyList<PrCommentThread>))]
[JsonSerializable(typeof(IReadOnlyList<AdoProject>))]
[JsonSerializable(typeof(IReadOnlyList<AdoRepo>))]
[JsonSerializable(typeof(PrActionOutcome))]
[JsonSerializable(typeof(PrDescriptionDraft))]
[JsonSerializable(typeof(AutoLinkResult))]
[JsonSerializable(typeof(PrLinkResolution))]
// Both unions nest a variant called NeedsToken, and the generator names its metadata by simple
// name — so the two are spelled out here. This changes no wire shape: the discriminator values
// come from [JsonDerivedType], not from these names.
[JsonSerializable(typeof(AutoLinkResult.NeedsToken), TypeInfoPropertyName = "AutoLinkNeedsToken")]
[JsonSerializable(typeof(PrLinkResolution.NeedsToken), TypeInfoPropertyName = "PrLinkNeedsToken")]
[JsonSerializable(typeof(PrActionMeta))]
internal sealed partial class ProviderJsonContext : JsonSerializerContext;
