using System.Collections.Concurrent;
using System.Text.Json;
using CodeFlow.Terminal;
using Xunit;

namespace CodeFlow.Tests.Terminal;

/// <summary>
/// A terminal session's life, against a real pseudo-terminal.
/// See <c>docs/business-rules/11-files-search-terminal.md</c> §PTY setup and §Read loop,
/// <c>FILE-015</c>.
/// </summary>
/// <remarks>
/// <para>
/// A real PTY and a real shell, like <c>PtyProbe</c>: the whole point of this feature is that a
/// pseudo-terminal works on this machine, and a double would prove nothing about that.
/// </para>
/// <para>
/// Serialised against the other tests that spawn processes — these launch shells, and a machine
/// under load makes the waits below flaky rather than the code wrong.
/// </para>
/// </remarks>
[Collection(SerialTemporaryFiles.Name)]
public sealed class TerminalSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_shell_runs_a_command_and_its_output_arrives_tagged_with_the_session_id()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows needs Git for Windows; unverified here.");

        await using var fixture = new Fixture();
        var id = await fixture.OpenAsync();

        await fixture.Terminals.WriteAsync(id, "echo marcador-de-prueba\n", Ct);

        var output = await fixture.WaitForOutputAsync("marcador-de-prueba");
        Assert.All(output, chunk => Assert.Equal(id, chunk.Id));
    }

    [Fact]
    public async Task Leaving_the_shell_reports_exactly_one_exit_and_it_comes_last()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows needs Git for Windows; unverified here.");

        await using var fixture = new Fixture();
        var id = await fixture.OpenAsync();

        // Enough output that the bounded channel is still draining when the shell leaves, rather
        // than a line or two the pipeline clears before the child even dies.
        await fixture.Terminals.WriteAsync(id, "seq 1 40000\necho ultima-linea\nexit\n", Ct);

        await fixture.WaitForExitAsync();

        Assert.Equal(1, fixture.ExitCount);
        Assert.Contains("ultima-linea", fixture.AllOutput, StringComparison.Ordinal);

        // FILE-015: the exit is published after the reader loop ends, so nothing can be queued
        // behind it. Honest caveat — publishing from Porta.Pty's ProcessExited instead was tried
        // here and this still passed, because on macOS that event arrives after the drain anyway.
        // What this pins is the guarantee, not a defect it once caught; the reason the guarantee is
        // worth pinning is Windows, which nothing here can exercise.
        Assert.Equal("terminal:exit", fixture.Events[^1].Name);
    }

    [Fact]
    public async Task Closing_a_session_stops_its_shell_and_reports_the_exit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows needs Git for Windows; unverified here.");

        await using var fixture = new Fixture();
        var id = await fixture.OpenAsync();

        await fixture.Terminals.CloseAsync(id);

        // CodeFlow 1.7.2 kills the child, which ends the reader loop, which emits one exit. A
        // deliberate close is not a special case.
        await fixture.WaitForExitAsync();
        Assert.Equal(1, fixture.ExitCount);
    }

    [Fact]
    public async Task A_session_can_be_resized_after_it_opens()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows needs Git for Windows; unverified here.");

        await using var fixture = new Fixture();
        var id = await fixture.OpenAsync();

        // The renderer sends (cols, rows) in that order on every fit. It does not answer anything;
        // what is asserted is that it reaches the pty without throwing.
        fixture.Terminals.Resize(id, 120, 40);
    }

    [Fact]
    public async Task Writing_to_a_session_nobody_opened_says_so()
    {
        await using var fixture = new Fixture();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Terminals.WriteAsync("no-such-session", "hola", Ct));

        Assert.Equal("no such terminal session", failure.Message);
    }

    [Fact]
    public async Task Resizing_a_session_nobody_opened_says_so()
    {
        await using var fixture = new Fixture();

        var failure = Assert.Throws<InvalidOperationException>(
            () => fixture.Terminals.Resize("no-such-session", 80, 24));

        Assert.Equal("no such terminal session", failure.Message);
    }

    [Fact]
    public async Task Closing_a_session_nobody_opened_is_not_an_error()
    {
        await using var fixture = new Fixture();

        // A no-op, exactly as 1.7.2's `if let Some(...)` makes it.
        await fixture.Terminals.CloseAsync("no-such-session");
    }

    /// <summary>A registry writing into a temporary directory, with every event it published.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("codeflow-term-").FullName;
        private readonly ConcurrentQueue<(string Name, string Id, string? Data)> _events = new();

        public Fixture() => Terminals = new TerminalRegistry(Record);

        public TerminalRegistry Terminals { get; }

        public IReadOnlyList<(string Name, string Id, string? Data)> Events => [.. _events];

        public int ExitCount => _events.Count(e => e.Name == "terminal:exit");

        public string AllOutput => string.Concat(_events.Where(e => e.Name == "terminal:output").Select(e => e.Data));

        public Task<string> OpenAsync() => Terminals.OpenAsync(_directory, Ct);

        /// <summary>Waits until the output published so far contains a marker.</summary>
        public async Task<IReadOnlyList<(string Name, string Id, string? Data)>> WaitForOutputAsync(string marker)
        {
            await WaitAsync(() => AllOutput.Contains(marker, StringComparison.Ordinal), $"output containing '{marker}'");
            return [.. _events.Where(e => e.Name == "terminal:output")];
        }

        public Task WaitForExitAsync() => WaitAsync(() => ExitCount > 0, "a terminal:exit");

        private async Task WaitAsync(Func<bool> until, string what)
        {
            // Generous, because it waits on a real shell starting: a slow machine should make this
            // slower, not red.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (until())
                {
                    return;
                }

                await Task.Delay(25, Ct);
            }

            Assert.Fail($"timed out waiting for {what}; published so far: {string.Join(", ", Events.Select(e => e.Name))}");
        }

        private ValueTask Record(string name, JsonElement payload, CancellationToken cancellationToken)
        {
            var id = payload.GetProperty("id").GetString()!;
            var data = payload.TryGetProperty("data", out var value) ? value.GetString() : null;
            _events.Enqueue((name, id, data));

            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await Terminals.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
