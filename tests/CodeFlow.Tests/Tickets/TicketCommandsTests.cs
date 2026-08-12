using CodeFlow.Ai;
using CodeFlow.Ipc;
using CodeFlow.Tickets;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// The command surface this feature owns.
/// </summary>
/// <remarks>
/// The names are the contract with <c>renderer/src/lib/ipc/commands.ts</c> and with
/// <c>docs/business-rules/01-ipc-surface.md</c>: a rename compiles on both sides and then reads as
/// "method not found" at runtime. Pinning the list is what makes that a failing test instead.
/// </remarks>
public sealed class TicketCommandsTests
{
    private static readonly string[] Expected =
    [
        "update_workspace_ticket_account",
        "resolve_ticket_account",
        "resolve_ticket_link",
        "suggest_ticket_for_branch",
        "sync_ticket",
        "get_ticket",
        "list_tickets",
        "get_ticket_criteria",
        "link_branch_ticket",
        "unlink_branch_ticket",
        "ticket_for_branch",
        "list_sprint_tickets",
        "list_my_tickets",
        "preview_ticket",
        "list_ticket_reviews",
        // One command for the four combinations of (scope × ticket). It replaced
        // `analyze_working_changes` and `review_branch_ticket`, and it is registered here rather
        // than in `Ai/` because `Tickets/` already depends on `Ai/` — the other way round would
        // close a cycle between two features.
        "review_changes",
        "comment_ticket",
    ];

    [Fact]
    public void The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        using var http = new HttpClient();
        var registry = new CommandRegistry().AddTicketCommands(database: null!, new AiRunRegistry((_, _, _) => ValueTask.CompletedTask), http);

        Assert.Equal(
            Expected.OrderBy(name => name, StringComparer.Ordinal),
            registry.Names.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void The_only_verb_here_that_writes_to_a_board_is_the_comment()
    {
        // What this slice may do to somebody's board is a property of it, not a coincidence of what
        // has been built so far. A comment is additive and undoes cleanly; a transition moves a card
        // other people are looking at, and its state names belong to the project's process rather
        // than to this app. Adding either has to change this test deliberately (`WI-022`).
        using var http = new HttpClient();
        var registry = new CommandRegistry().AddTicketCommands(database: null!, new AiRunRegistry((_, _, _) => ValueTask.CompletedTask), http);

        Assert.Contains("comment_ticket", registry.Names);

        Assert.DoesNotContain(registry.Names, name =>
            name.Contains("transition", StringComparison.Ordinal)
            || name.Contains("set_ticket_state", StringComparison.Ordinal)
            || name.Contains("close_ticket", StringComparison.Ordinal));
    }
}
