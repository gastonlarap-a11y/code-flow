using System.Globalization;
using CodeFlow.Providers.Azure;
using CodeFlow.Security;
using CodeFlow.Tickets;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// The one call in this feature that writes to a real board, against a real board.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="AzureBoardsEndToEndTests"/> on purpose.</b> That class states, and
/// keeps, that nothing in it writes anywhere; folding a write into it would quietly retire a
/// guarantee somebody relies on when they run the suite against their employer's organisation.
/// </para>
/// <para>
/// Two gates, not one. <c>CODEFLOW_E2E_ADO_ORG</c> is the same variable the read-only suite uses,
/// and <c>CODEFLOW_E2E_ADO_WRITE_WORKITEM</c> has to name the <b>exact</b> work item that may be
/// commented on — not a project, not "the first one found". Running the E2E category without that
/// second variable writes nothing, so the read-only run stays read-only by default:
/// <code>
/// CODEFLOW_E2E_ADO_ORG=your-org CODEFLOW_E2E_ADO_WRITE_WORKITEM=3 \
///   dotnet test CodeFlow.slnx --configuration Release --no-build --filter "Category=E2E"
/// </code>
/// </para>
/// <para>
/// It exists because the fake transport cannot catch what actually breaks a write: the comments
/// endpoint is preview-only and rejects a plain <c>7.1</c>, and it takes HTML rather than the
/// markdown the verdict is written in. Both compile, both pass against a fake, and both are only
/// visible on a board.
/// </para>
/// <para>
/// It leaves a comment behind, deliberately marked as a test so whoever finds it knows what it is.
/// A comment is the one write that undoes cleanly — which is why it is the only one built.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class AzureCommentEndToEndTests
{
    private const string OrgVariable = "CODEFLOW_E2E_ADO_ORG";

    private const string ProjectVariable = "CODEFLOW_E2E_ADO_PROJECT";

    /// <summary>Names the single work item this test may comment on.</summary>
    private const string WorkItemVariable = "CODEFLOW_E2E_ADO_WRITE_WORKITEM";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_verdict_posted_as_a_comment_comes_back_rendered()
    {
        var (org, project, id, pat) = Target();

        // Shaped like a real verdict, because the point is the conversion surviving the round trip:
        // the two heading levels, bold, inline code, the rule and the footer.
        var markdown = string.Create(
            CultureInfo.InvariantCulture,
            $"""
            ## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN

            ### AC-1: prueba automatizada de CodeFlow
            Veredicto: **no verificable**
            Evidencia: `AzureCommentEndToEndTests.cs` — a < b && c > d

            ---
            🤖 Comentario de prueba de CodeFlow ({DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC). Se puede borrar.
            """);

        using var http = new HttpClient();

        var created = await AzureWorkItemClient.AddCommentAsync(
            http, org, project, id, TicketComment.ToHtml(markdown), pat, Ct);

        Assert.True(created.Id > 0, "Azure must answer with the comment it created");

        // Read it back through the client's own reader rather than trusting the POST's echo: what
        // matters is what the board will show the next person who opens the work item.
        var comments = await AzureWorkItemClient.ListCommentsAsync(http, org, project, id, pat, Ct);
        var posted = comments.FirstOrDefault(c => c.Id == created.Id);

        Assert.NotNull(posted);
        Assert.Contains("VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN", posted.Text ?? string.Empty, StringComparison.Ordinal);

        // The two failures a fake cannot catch. Markdown punctuation surviving means the HTML never
        // arrived as HTML; an unescaped `<` means a review quoting code could inject markup.
        Assert.DoesNotContain("## ", posted.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("**", posted.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("a &lt; b", posted.Text ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The organisation, project, work item and PAT — or a skip saying what is missing.</summary>
    private static (string Org, string Project, long Id, string Pat) Target()
    {
        var org = Environment.GetEnvironmentVariable(OrgVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(org),
            $"Needs a real Azure DevOps organisation. Set {OrgVariable} to run this.");

        var raw = Environment.GetEnvironmentVariable(WorkItemVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(raw),
            $"This test writes a comment. Set {WorkItemVariable} to the id of a work item it may "
            + "comment on — nothing is written without it, which is what keeps a plain E2E run "
            + "read-only.");

        Assert.SkipUnless(
            long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id),
            $"{WorkItemVariable} must be a work item number; got '{raw}'.");

        Assert.SkipUnless(
            OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(),
            "The PAT is read from the OS credential store, which exists only on macOS and Windows.");

        var pat = CredentialStore.Get(CredentialStore.AdoPatKey(org!));
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(pat),
            $"No PAT under '{CredentialStore.AdoPatKey(org!)}' in the OS credential store. Connect "
            + $"'{org}' in CodeFlow's Settings first.");

        var project = Environment.GetEnvironmentVariable(ProjectVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(project),
            $"Set {ProjectVariable} to the project holding work item {id}. Unlike the read-only "
            + "suite this does not go looking: a write picks its target explicitly or not at all.");

        return (org!, project!, id, pat!);
    }
}
