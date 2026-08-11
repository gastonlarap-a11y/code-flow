using CodeFlow.Providers;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// The several shapes a work item's address arrives in when somebody pastes or types it.
/// </summary>
/// <remarks>
/// The URLs here are real ones, copied from a live organisation. A parser for pasted text is only
/// worth what its inputs are worth.
/// </remarks>
public sealed class WorkItemLinkTests
{
    [Theory]
    // The canonical page.
    [InlineData("https://dev.azure.com/achsdev/Web/_workitems/edit/426647", 426647, "achsdev", "Web")]
    // The legacy host, which still serves the modern UI and is what the browser shows.
    [InlineData("https://achsdev.visualstudio.com/Web/_workitems/edit/426647", 426647, "achsdev", "Web")]
    // Without the /edit/ step.
    [InlineData("https://dev.azure.com/achsdev/Web/_workitems/426647", 426647, "achsdev", "Web")]
    // A trailing slash, a fragment and a query the path form does not need.
    [InlineData("https://dev.azure.com/achsdev/Web/_workitems/edit/426647/?_a=history#c1", 426647, "achsdev", "Web")]
    public void A_work_item_page_gives_up_all_three_parts(string url, long id, string org, string project)
    {
        var reference = WorkItemLink.Parse(url);

        Assert.NotNull(reference);
        Assert.Equal(id, reference.Id);
        Assert.Equal(org, reference.Org);
        Assert.Equal(project, reference.Project);
    }

    [Fact]
    public void A_taskboard_url_is_read_from_its_query_not_its_path()
    {
        // The URL most likely to be in the clipboard: it is the page you were looking at when you
        // decided to link the branch. PrLink's splitter discards the query, which is why this parser
        // does not reuse it.
        var url = "https://achsdev.visualstudio.com/ICL-SAL-Admision/_sprints/taskboard/"
            + "Equipo%20Siniestro/ICL-SAL-Admision/Sprint%2035%20-%20TBD?workitem=426647";

        var reference = WorkItemLink.Parse(url);

        Assert.NotNull(reference);
        Assert.Equal(426647, reference.Id);
        Assert.Equal("achsdev", reference.Org);
        Assert.Equal("ICL-SAL-Admision", reference.Project);
    }

    [Fact]
    public void A_project_name_with_percent_encoded_spaces_comes_back_decoded()
    {
        var reference = WorkItemLink.Parse("https://dev.azure.com/achsdev/Ficha%20Clinica/_workitems/edit/1");

        Assert.NotNull(reference);
        Assert.Equal("Ficha Clinica", reference.Project);
    }

    [Theory]
    [InlineData("426647")]
    [InlineData("  426647  ")]
    [InlineData("AB#426647")]
    [InlineData("ab#426647")]
    public void A_bare_id_is_accepted_and_leaves_the_rest_to_be_filled_in(string input)
    {
        // Typing the number is the fastest way to link a ticket you already know, so it must work —
        // and the organisation and project then come from the workspace, which knows them.
        var reference = WorkItemLink.Parse(input);

        Assert.NotNull(reference);
        Assert.Equal(426647, reference.Id);
        Assert.Null(reference.Org);
        Assert.Null(reference.Project);
    }

    [Fact]
    public void An_organisation_scoped_link_has_no_project_to_report()
    {
        var reference = WorkItemLink.Parse("https://dev.azure.com/achsdev/_workitems/edit/426647");

        Assert.NotNull(reference);
        Assert.Equal(426647, reference.Id);
        Assert.Equal("achsdev", reference.Org);
        Assert.Null(reference.Project);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://dev.azure.com/achsdev/Web/_git/repo/pullrequest/12")]
    [InlineData("https://example.com/whatever")]
    [InlineData("not a link at all")]
    // A Jira key belongs to a provider that is not connected. Answering with an Azure-shaped
    // reference would send the app looking for a work item numbered 45.
    [InlineData("PROJ-45")]
    public void Anything_that_is_not_a_work_item_is_refused(string input)
    {
        Assert.Null(WorkItemLink.Parse(input));
    }
}
