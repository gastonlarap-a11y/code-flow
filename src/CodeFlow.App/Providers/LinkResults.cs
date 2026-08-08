using System.Text.Json.Serialization;
using CodeFlow.Workspaces;

namespace CodeFlow.Providers;

/// <summary>
/// What auto-detecting a project's pull-request host concluded.
/// </summary>
/// <remarks>
/// <para>
/// A tagged union on the wire: the discriminator is <c>status</c> and its values are the variant
/// names exactly as written. The renderer switches on those strings.
/// </para>
/// <para>
/// Note the naming policy applies to <em>properties</em> (<c>project_id</c>, <c>clone_url</c>) and not
/// to the discriminator values, which stay PascalCase. Getting that backwards compiles, serialises, and
/// then silently fails every <c>switch</c> in the UI — hence a round-trip test per variant.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(Linked), "Linked")]
[JsonDerivedType(typeof(NeedsToken), "NeedsToken")]
[JsonDerivedType(typeof(NotDetected), "NotDetected")]
public abstract record AutoLinkResult
{
    private AutoLinkResult()
    {
    }

    /// <summary>A host was recognised and a token for it was already saved, so the project is linked.</summary>
    public sealed record Linked(Project Project) : AutoLinkResult;

    /// <summary>
    /// A host was recognised but has no saved credential.
    /// </summary>
    /// <param name="Identifier">
    /// <b>The GitHub repository's owner here, but its host in <see cref="PrLinkResolution.NeedsToken"/>.</b>
    /// The two flows fill the same field with different things in 1.7.2. Reproduced rather than
    /// unified: the UI pairs it with a translated label per flow, so making them agree would change what
    /// one of the two screens reads.
    /// </param>
    public sealed record NeedsToken(string Provider, string Identifier) : AutoLinkResult;

    /// <summary>No remote matched a host this app can talk to.</summary>
    public sealed record NotDetected : AutoLinkResult;
}

/// <summary>
/// What a pasted pull-request link turned out to be.
/// </summary>
/// <remarks>
/// The point of the flow is that the link alone is enough: the user never has to know which of their
/// repositories it belongs to, nor find it in the sidebar first.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(Ready), "Ready")]
[JsonDerivedType(typeof(NeedsToken), "NeedsToken")]
[JsonDerivedType(typeof(Expired), "Expired")]
[JsonDerivedType(typeof(NoLocalRepo), "NoLocalRepo")]
[JsonDerivedType(typeof(Unrecognized), "Unrecognized")]
public abstract record PrLinkResolution
{
    private PrLinkResolution()
    {
    }

    /// <summary>
    /// The link resolved to a pull request <em>and</em> to a local repository, now linked to that host.
    /// </summary>
    /// <remarks>
    /// Selecting this PR then gives the full pipeline — local diff, findings, comments — identical to
    /// picking it in the sidebar.
    /// </remarks>
    public sealed record Ready(
        string ProjectId,
        string WorkspaceId,
        string ProjectName,
        PullRequestSummary Pr) : PrLinkResolution;

    /// <summary>
    /// The link is for a host with no saved credential, so the PR could not even be fetched.
    /// </summary>
    /// <param name="Identifier">
    /// The GitHub <em>host</em> here — see the note on <see cref="AutoLinkResult.NeedsToken"/>, which
    /// carries the owner instead.
    /// </param>
    public sealed record NeedsToken(string Provider, string Identifier) : PrLinkResolution;

    /// <summary>
    /// A credential is saved for the host, and the host refused it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DIVERGENCE-PROV-b</c>. Distinct from <see cref="NeedsToken"/> because the two need different
    /// sentences and lead to different actions: one is "connect this organisation", the other is "the
    /// token you already saved no longer works". Collapsing them would tell a user with an expired PAT
    /// to do something they have already done.
    /// </para>
    /// <para>
    /// Azure DevOps only, for now. GitHub tokens expire too, but <c>.claude/rules/dotnet.md</c> singles out
    /// Azure — organisation policy caps PAT lifetime there and global PATs are being retired — and
    /// widening this without a reason to would be inventing scope.
    /// </para>
    /// </remarks>
    public sealed record Expired(string Provider, string Identifier) : PrLinkResolution;

    /// <summary>
    /// The pull request was read, but no local repository on this machine matches it.
    /// </summary>
    /// <remarks>
    /// The UI offers to clone it — hence the label and the clone URL travelling with the PR.
    /// </remarks>
    public sealed record NoLocalRepo(
        string Provider,
        string RepoLabel,
        string CloneUrl,
        PullRequestSummary Pr) : PrLinkResolution;

    /// <summary>
    /// The text is not a pull-request URL on any host this app knows.
    /// </summary>
    /// <remarks>
    /// A normal answer, not a failure — which is why this flow returns a variant where the
    /// <c>pr_link_*</c> commands throw instead. An unconnected Enterprise host lands here too, since it
    /// is indistinguishable from any other self-hosted server.
    /// </remarks>
    public sealed record Unrecognized : PrLinkResolution;
}
