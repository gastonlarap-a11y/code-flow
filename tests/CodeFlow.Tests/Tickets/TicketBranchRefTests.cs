using CodeFlow.Tickets;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// The branch-name heuristic that suggests which ticket a branch is work for.
/// </summary>
/// <remarks>
/// Table-driven against real-shaped branch names: the only way to tell a pattern that is useful from
/// one that fires on <c>release/2025-cleanup</c> is to write both down and look at them together.
/// </remarks>
public sealed class TicketBranchRefTests
{
    [Theory]
    // Azure's own smart-reference syntax wins wherever it appears, in either case.
    [InlineData("feature/AB#1234-login-sso", "azure", "1234")]
    [InlineData("ab#77", "azure", "77")]
    // The common conventions: a leading number on the branch's own segment.
    [InlineData("1234-login-sso", "azure", "1234")]
    [InlineData("feature/1234-login-sso", "azure", "1234")]
    [InlineData("bugfix/9", "azure", "9")]
    [InlineData("feature/1234_login", "azure", "1234")]
    [InlineData("refs/heads/feature/1234-login", "azure", "1234")]
    // Jira keys are recognised so the caller can say "Jira is not connected" instead of looking up
    // an Azure work item numbered 45.
    [InlineData("feature/PROJ-45-login", "jira", "PROJ-45")]
    [InlineData("PROJ2-100", "jira", "PROJ2-100")]
    public void A_recognised_branch_names_its_ticket(string branch, string provider, string id)
    {
        var reference = Assert.NotNull(TicketBranchRef.Detect(branch));

        Assert.Equal(provider, reference.Provider);
        Assert.Equal(id, reference.ExternalId);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("feature/login-with-sso")]
    [InlineData("")]
    [InlineData("   ")]
    // The number is not at the start of the segment, so it is a version or a count, not a ticket.
    [InlineData("feature/login-v2")]
    [InlineData("chore/bump-node-25")]
    // Lower case is not a Jira key: this one is a character encoding.
    [InlineData("feature/utf-8-encoding")]
    public void A_branch_with_no_ticket_in_its_name_suggests_nothing(string branch)
    {
        Assert.Null(TicketBranchRef.Detect(branch));
    }

    [Fact]
    public void A_prefix_segment_is_not_mistaken_for_the_ticket()
    {
        // `users/gaston/1234-login` is about 1234, not about "gaston".
        var reference = Assert.NotNull(TicketBranchRef.Detect("users/gaston/1234-login"));

        Assert.Equal("1234", reference.ExternalId);
    }

    [Fact]
    public void A_date_led_branch_is_the_accepted_false_positive()
    {
        // Documented in Detect's remarks: nothing in the name separates a year from a work-item
        // number. Pinned as a test so the behaviour is a decision on record rather than a surprise
        // somebody later "fixes" without seeing what it costs.
        var reference = Assert.NotNull(TicketBranchRef.Detect("release/2025-cleanup"));

        Assert.Equal("2025", reference.ExternalId);
    }
}
