using System.Text.Json;
using CodeFlow.Providers;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// Which host a project dispatches to, and the shapes the link flows put on the wire.
/// See <c>docs/business-rules/07-review-pipeline.md</c> <c>REVIEW-001</c>.
/// </summary>
public sealed class LinkedRepoTests
{
    [Fact]
    public void A_github_linked_project_resolves_to_github()
    {
        var link = Assert.IsType<LinkedRepo.GitHub>(LinkedRepo.Resolve(Project(owner: "acme", repo: "widget")));

        Assert.Equal("acme", link.Owner);
        Assert.Equal("widget", link.Repo);
        // A null host means github.com, not "unset".
        Assert.Equal("github.com", link.Host);
    }

    [Fact]
    public void An_explicit_enterprise_host_is_carried_through()
    {
        var link = Assert.IsType<LinkedRepo.GitHub>(
            LinkedRepo.Resolve(Project(owner: "team", repo: "app", host: "ghe.contoso.com")));

        Assert.Equal("ghe.contoso.com", link.Host);
    }

    [Fact]
    public void An_ado_linked_project_resolves_to_azure()
    {
        var link = Assert.IsType<LinkedRepo.Azure>(
            LinkedRepo.Resolve(Project(org: "contoso", adoProject: "Web", repoId: "api")));

        Assert.Equal("contoso", link.Org);
        Assert.Equal("Web", link.Project);
        Assert.Equal("api", link.RepoId);
    }

    [Fact]
    public void Github_wins_when_a_project_carries_both_links()
    {
        // REVIEW-001. Load-bearing rather than incidental: the link flows clear both column sets before
        // writing one precisely because a mixed row would dispatch here.
        var both = Project(owner: "acme", repo: "widget", org: "contoso", adoProject: "Web", repoId: "api");

        Assert.IsType<LinkedRepo.GitHub>(LinkedRepo.Resolve(both));
    }

    [Fact]
    public void A_half_filled_link_does_not_count()
    {
        // Owner without repo, and two of Azure's three columns: neither resolves, because a partial link
        // cannot address a pull request.
        Assert.Throws<ProviderException>(() => LinkedRepo.Resolve(Project(owner: "acme")));
        Assert.Throws<ProviderException>(() => LinkedRepo.Resolve(Project(org: "contoso", adoProject: "Web")));
    }

    [Fact]
    public void An_unlinked_project_says_so_in_the_words_the_frontend_shows()
    {
        var failure = Assert.Throws<ProviderException>(() => LinkedRepo.Resolve(Project()));

        Assert.Equal("This project isn't linked to a pull-request host yet", failure.Message);
    }

    // ---------- storing a link ----------

    [Fact]
    public void Linking_one_provider_leaves_the_other_columns_alone()
    {
        // Deliberate one-sidedness, reproduced: it is why every re-link unlinks first.
        using var db = new TempDatabase();
        var id = Create(db);

        db.Do(c => ProjectStore.LinkAdo(c, id, "contoso", "Web", "api"));
        db.Do(c => ProjectStore.LinkGithub(c, id, "acme", "widget", "github.com"));

        var project = db.Use(c => ProjectStore.Get(c, id))!;

        Assert.Equal("acme", project.GithubOwner);
        Assert.Equal("contoso", project.AdoOrg);
        // And with both sets present, dispatch goes to GitHub.
        Assert.IsType<LinkedRepo.GitHub>(LinkedRepo.Resolve(project));
    }

    [Fact]
    public void Unlinking_clears_all_six_columns_whichever_was_set()
    {
        using var db = new TempDatabase();
        var id = Create(db);

        db.Do(c => ProjectStore.LinkGithub(c, id, "acme", "widget", "github.com"));
        db.Do(c => ProjectStore.LinkAdo(c, id, "contoso", "Web", "api"));
        db.Do(c => ProjectStore.Unlink(c, id));

        var project = db.Use(c => ProjectStore.Get(c, id))!;

        Assert.Null(project.GithubOwner);
        Assert.Null(project.GithubRepo);
        Assert.Null(project.GithubHost);
        Assert.Null(project.AdoOrg);
        Assert.Null(project.AdoProject);
        Assert.Null(project.AdoRepoId);
    }

    // ---------- the wire shapes ----------

    [Fact]
    public void Every_auto_link_variant_carries_the_discriminator_the_renderer_switches_on()
    {
        Assert.Equal("Linked", Status(new AutoLinkResult.Linked(Project(owner: "a", repo: "b"))));
        Assert.Equal("NeedsToken", Status(new AutoLinkResult.NeedsToken("github", "acme")));
        Assert.Equal("NotDetected", Status(new AutoLinkResult.NotDetected()));
    }

    [Fact]
    public void Every_pr_link_variant_carries_the_discriminator_the_renderer_switches_on()
    {
        // Four variants, not three: Unrecognized is what a link that is not a PR resolves to, and it is
        // easy to miss because it has no fields.
        Assert.Equal("Ready", Status(new PrLinkResolution.Ready("p", "w", "Name", Pr())));
        Assert.Equal("NeedsToken", Status(new PrLinkResolution.NeedsToken("github", "github.com")));
        Assert.Equal("NoLocalRepo", Status(new PrLinkResolution.NoLocalRepo("github", "acme/widget", "url", Pr())));
        Assert.Equal("Unrecognized", Status(new PrLinkResolution.Unrecognized()));
    }

    [Fact]
    public void A_variants_own_fields_stay_snake_case_while_its_tag_stays_pascal_case()
    {
        // The mix is 1.7.2's, and getting it backwards compiles and then silently breaks every
        // switch in the UI.
        using var payload = JsonSerializer.SerializeToDocument(
            (PrLinkResolution)new PrLinkResolution.NoLocalRepo("github", "acme/widget", "https://…", Pr()),
            ProviderJsonContext.Default.PrLinkResolution);

        Assert.Equal(
            ["status", "provider", "repo_label", "clone_url", "pr"],
            payload.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void A_ready_resolution_names_the_project_the_way_the_renderer_reads_it()
    {
        using var payload = JsonSerializer.SerializeToDocument(
            (PrLinkResolution)new PrLinkResolution.Ready("p1", "w1", "Repo", Pr()),
            ProviderJsonContext.Default.PrLinkResolution);

        Assert.Equal(
            ["status", "project_id", "workspace_id", "project_name", "pr"],
            payload.RootElement.EnumerateObject().Select(p => p.Name));
    }

    private static string Status(AutoLinkResult value) =>
        Discriminator(JsonSerializer.SerializeToDocument(value, ProviderJsonContext.Default.AutoLinkResult));

    private static string Status(PrLinkResolution value) =>
        Discriminator(JsonSerializer.SerializeToDocument(value, ProviderJsonContext.Default.PrLinkResolution));

    private static string Discriminator(JsonDocument payload)
    {
        using (payload)
        {
            return payload.RootElement.GetProperty("status").GetString()!;
        }
    }

    private static PullRequestSummary Pr() =>
        new(42, "Add the thing", "why", "open", "feature", "main", "octocat", "t", "u", "github");

    private static string Create(TempDatabase db)
    {
        var workspace = db.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#6366f1"));
        return db.Use(c => ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id))).Id;
    }

    private static Project Project(
        string? owner = null,
        string? repo = null,
        string? host = null,
        string? org = null,
        string? adoProject = null,
        string? repoId = null) =>
        new("p1", "w1", "Repo", "/tmp/repo", null, "#000", "git-branch",
            org, adoProject, repoId, owner, repo, host, 0, "2026-07-29T00:00:00.0000000+00:00");
}
