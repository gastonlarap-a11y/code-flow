using System.Text.Json;
using CodeFlow.Ipc;
using CodeFlow.Terminal;
using Xunit;

namespace CodeFlow.Tests.Terminal;

/// <summary>
/// The four commands from the implementation, as the transport reaches them.
/// See <c>docs/business-rules/01-ipc-surface.md</c> and <c>11-files-search-terminal.md</c>.
/// </summary>
public sealed class TerminalCommandsTests
{
    /// <summary>The exact set this group registers.</summary>
    private static readonly string[] Expected =
    [
        "open_terminal", "write_terminal", "resize_terminal", "close_terminal",
    ];

    [Fact]
    public async Task The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        await using var terminals = new TerminalRegistry((_, _, _) => ValueTask.CompletedTask);
        var registry = new CommandRegistry().AddTerminalCommands(terminals);

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Writing_to_a_session_nobody_opened_says_so_over_the_wire()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync("write_terminal", new { id = "no-such-session", data = "hola" }).AsTask());

        Assert.Equal("no such terminal session", failure.Message);
    }

    [Fact]
    public async Task Closing_a_session_nobody_opened_answers_null()
    {
        // A no-op, not an error, exactly as 1.7.2's `if let Some(...)` makes it.
        Assert.Equal("null", await InvokeAsync("close_terminal", new { id = "no-such-session" }));
    }

    [Theory]
    [InlineData("open_terminal", "cwd")]
    [InlineData("write_terminal", "id")]
    [InlineData("close_terminal", "id")]
    public async Task A_command_missing_its_argument_names_the_one_it_wanted(string command, string missing)
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(command, new { }).AsTask());

        Assert.Equal($"missing required parameter '{missing}'", failure.Message);
    }

    [Fact]
    public async Task Resize_needs_both_of_its_numbers()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync("resize_terminal", new { id = "any", cols = 120 }).AsTask());

        Assert.Equal("missing required parameter 'rows'", failure.Message);
    }

    /// <summary>Dispatches a command the way the transport does, and answers its JSON reply.</summary>
    private static async ValueTask<string> InvokeAsync(string command, object parameters)
    {
        await using var terminals = new TerminalRegistry((_, _, _) => ValueTask.CompletedTask);
        var registry = new CommandRegistry().AddTerminalCommands(terminals);
        Assert.True(registry.TryGet(command, out var handler));

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var reply = await handler(arguments.RootElement, TestContext.Current.CancellationToken);

        return System.Text.Encoding.UTF8.GetString(reply.Span);
    }
}
