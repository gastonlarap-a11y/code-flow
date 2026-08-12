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
    public void Nothing_registered_here_writes_to_azure()
    {
        // Read-only is a property of this slice, not a coincidence of what has been built so far.
        // The write verbs are named so that adding one has to change this test deliberately.
        using var http = new HttpClient();
        var registry = new CommandRegistry().AddTicketCommands(database: null!, new AiRunRegistry((_, _, _) => ValueTask.CompletedTask), http);

        Assert.DoesNotContain(registry.Names, name =>
            name.Contains("comment", StringComparison.Ordinal)
            || name.Contains("transition", StringComparison.Ordinal)
            || name.Contains("set_ticket_state", StringComparison.Ordinal));
    }
}
