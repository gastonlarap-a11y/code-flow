using CodeFlow.Providers;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// Splitting the model's drafted description into a title and a body.
/// </summary>
/// <remarks>
/// The prompt tells the model the first line must be <c>TITLE: …</c>, and a model will sometimes not
/// comply — so what this does when the marker is missing, indented, or not the first line is the whole
/// behaviour worth pinning.
/// </remarks>
public sealed class PrDescriptionDraftTests
{
    [Fact]
    public void The_title_line_is_lifted_out_and_the_body_keeps_the_rest()
    {
        var draft = ProviderCommands.ParseDraft(
            """
            TITLE: feat(api): add the thing

            ## Resumen
            Hace la cosa.
            """);

        Assert.Equal("feat(api): add the thing", draft.Title);
        // The blank line the template puts after the title is gone: the body is trimmed as a whole.
        Assert.Equal("## Resumen\nHace la cosa.", draft.Body);
    }

    [Fact]
    public void An_indented_marker_still_counts()
    {
        // The check runs on the line with its leading whitespace stripped, so a model that indents its
        // first line still gets a title rather than having it swallowed into the body.
        var draft = ProviderCommands.ParseDraft("   TITLE: still a title\n\nbody");

        Assert.Equal("still a title", draft.Title);
        Assert.Equal("body", draft.Body);
    }

    [Fact]
    public void The_marker_is_case_sensitive()
    {
        // strip_prefix in 1.7.2 is an exact byte match, so "Title:" is not the marker — the whole
        // text becomes the body and the form is left for the user.
        var draft = ProviderCommands.ParseDraft("Title: not the marker\n\nbody");

        Assert.Equal(string.Empty, draft.Title);
        Assert.Equal("Title: not the marker\n\nbody", draft.Body);
    }

    [Fact]
    public void A_marker_that_is_not_the_first_line_is_still_found_and_the_lines_above_it_are_kept()
    {
        // The loop keeps scanning, and lines before the marker go into the body in their original order —
        // so a model that prefixes an apology does not lose it, and does not lose its title either.
        var draft = ProviderCommands.ParseDraft(
            """
            Here is the description:
            TITLE: the real title
            the body
            """);

        Assert.Equal("the real title", draft.Title);
        Assert.Equal("Here is the description:\nthe body", draft.Body);
    }

    [Fact]
    public void Only_the_first_marker_is_consumed()
    {
        var draft = ProviderCommands.ParseDraft("TITLE: first\nTITLE: second\n");

        Assert.Equal("first", draft.Title);
        // The second one is ordinary body text, not a competing title.
        Assert.Equal("TITLE: second", draft.Body);
    }

    [Fact]
    public void No_marker_at_all_leaves_the_title_empty_rather_than_guessing_one()
    {
        var draft = ProviderCommands.ParseDraft("  just a body, no marker  ");

        Assert.Equal(string.Empty, draft.Title);
        Assert.Equal("just a body, no marker", draft.Body);
    }

    [Fact]
    public void An_empty_title_after_the_marker_is_an_empty_title_not_a_missing_one()
    {
        var draft = ProviderCommands.ParseDraft("TITLE:   \n\nthe body");

        Assert.Equal(string.Empty, draft.Title);
        // And crucially the marker line is still consumed, so it does not leak into the body.
        Assert.Equal("the body", draft.Body);
    }

    [Fact]
    public void Everything_empty_stays_empty()
    {
        var draft = ProviderCommands.ParseDraft("   \n  \n");

        Assert.Equal(string.Empty, draft.Title);
        Assert.Equal(string.Empty, draft.Body);
    }
}
