using System.Diagnostics;
using CodeFlow.Ai;
using CodeFlow.Ai.Engines;
using CodeFlow.Ipc;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// The agent-streaming risk item, exercised against a real subprocess.
/// </summary>
/// <remarks>
/// <para>
/// What is being proven, per <c>docs/business-rules/90-ambiguities.md</c> rather than <c>.claude/rules/dotnet.md</c>:
/// line-level streaming into the activity log, cancellation mid-stream, and correct extraction of
/// the terminal <c>result</c> event. <b>Not</b> incremental answer rendering — the app does not do
/// that, and proving it would prove a feature §9 forbids adding.
/// </para>
/// <para>
/// The streaming and cancellation mechanics run against a scripted process, so they are
/// deterministic and need no network, model or account. A separate test invokes the real
/// <c>claude</c> CLI when it is installed, because a scripted process cannot prove the flags are
/// accepted.
/// </para>
/// </remarks>
public sealed class AgentStreamingTests
{
    [Fact]
    public async Task Every_line_a_process_prints_is_streamed_as_it_happens()
    {
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        // Three lines with a pause between them: if the pump batched until exit, the third would
        // still arrive, but the timing assertion below would not hold.
        var outcome = await registry.RunAsync(
            new AiRunContext("run-1"),
            Script("printf 'one\\n'; printf 'two\\n'; printf 'three\\n'"),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Success);
        Assert.Equal(["one", "two", "three"], published.Lines("run-1", "stdout"));

        // The captured output is what interpretation later reads; streaming must not consume it.
        Assert.Contains("two", outcome.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_payload_is_spelled_the_way_the_renderer_reads_it()
    {
        // events.ts:32 declares { run_id, stream, line }. Arguments travel camelCase
        // arguments on the way in, but payloads keep their own names on the way out, so an event
        // payload keeps its snake_case field names. Source generation does not know that, and the default
        // camelCase policy silently produced runId — a field the UI reads as undefined.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        await registry.RunAsync(new AiRunContext("run-0"), Script("printf 'hello\\n'"), TestContext.Current.CancellationToken);

        // The first payload, not the only one: asserting on Single here made the test depend on the
        // scripted shell never writing to stderr, which it is not obliged to. Every payload has the
        // same three fields whichever stream it came from, so the contract is provable either way.
        var payloads = published.Payloads("ai:output");
        Assert.NotEmpty(payloads);
        Assert.Equal(["run_id", "stream", "line"], payloads[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["hello"], published.Lines("run-0", "stdout"));
    }

    [Fact]
    public async Task Stdout_and_stderr_are_tagged_separately()
    {
        // The UI dims stderr, because most CLIs use it for progress chatter rather than failures.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        await registry.RunAsync(
            new AiRunContext("run-2"),
            Script("printf 'answer\\n'; printf 'progress\\n' 1>&2"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["answer"], published.Lines("run-2", "stdout"));
        Assert.Equal(["progress"], published.Lines("run-2", "stderr"));
    }

    [Fact]
    public async Task A_run_can_be_cancelled_mid_stream()
    {
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        // Cancellation is an ordinary command against a registry keyed by run id — the transport
        // has no cancel frame and needs none.
        var running = registry.RunAsync(
            new AiRunContext("run-3"),
            Script("printf 'started\\n'; sleep 30; printf 'never\\n'"),
            TestContext.Current.CancellationToken);

        for (var attempt = 0; attempt < 100 && published.Lines("run-3", "stdout").Count == 0; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["started"], published.Lines("run-3", "stdout"));
        Assert.True(registry.Cancel("run-3"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.DoesNotContain("never", published.Lines("run-3", "stdout"));
    }

    [Fact]
    public void Cancelling_an_unknown_run_reports_that_nothing_was_in_flight()
    {
        // The command returns bool, and it means "a run with that id existed", not "confirmed
        // stopped" — making the caller wait for subprocess cleanup would stall the UI.
        var (publish, _) = Recorder.Create();
        Assert.False(new AiRunRegistry(publish).Cancel("never-started"));
    }

    [Fact]
    public async Task Cancelling_a_run_that_just_ended_answers_instead_of_throwing()
    {
        // The stop signal and the run's own ending race: `RunAsync` removes the entry and disposes
        // its source on the way out, and `Cancel` may already hold that reference. Cancelling a
        // disposed source throws, and the throw used to travel out of `cancel_ai_run` as a command
        // failure for a run that had simply finished.
        var (publish, _) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        await registry.RunAsync(
            new AiRunContext("run-done"), Script("printf 'done\\n'"), TestContext.Current.CancellationToken);

        Assert.False(registry.Cancel("run-done"));
    }

    [Fact]
    public async Task A_run_that_never_ends_is_cut_short_by_its_own_deadline()
    {
        // Nothing bounded a run before this: the wait was linked only to the caller's token, so a
        // CLI that never exits left the panel spinning with no way out but the stop button. The
        // deadline is injected here because the real one is ten minutes.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish, TimeSpan.FromMilliseconds(300));

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => registry.RunAsync(
            new AiRunContext("run-slow"),
            Script("printf 'started\\n'; sleep 30; printf 'never\\n'"),
            TestContext.Current.CancellationToken));

        // The marker, not a cancellation: the user pressed nothing and must not be told they did.
        Assert.StartsWith(AiRunRegistry.TimedOutMarker, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AiRunRegistry.CancelledMarker, failure.Message, StringComparison.Ordinal);

        // A deadline under a minute names no duration; the renderer has wording for that.
        Assert.Equal(AiRunRegistry.TimedOutMarker, failure.Message);

        Assert.DoesNotContain("never", published.Lines("run-slow", "stdout"));

        // And the run is no longer stoppable, because it is no longer in flight.
        Assert.False(registry.Cancel("run-slow"));
    }

    [Fact]
    public async Task A_stop_still_reads_as_a_stop_when_a_deadline_is_also_set()
    {
        // Both sources feed one linked token, so the catch has to ask which of the two fired.
        // Getting that backwards would relabel every stop as a timeout.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish, TimeSpan.FromMinutes(5));

        var running = registry.RunAsync(
            new AiRunContext("run-stopped"),
            Script("printf 'started\\n'; sleep 30; printf 'never\\n'"),
            TestContext.Current.CancellationToken);

        for (var attempt = 0; attempt < 100 && published.Lines("run-stopped", "stdout").Count == 0; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(registry.Cancel("run-stopped"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task A_very_long_line_is_capped_before_it_crosses_the_boundary()
    {
        // A CLI drawing a progress bar can produce megabytes on one "line", and the UI only ever
        // shows a tail anyway.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        await registry.RunAsync(
            new AiRunContext("run-4"),
            Script("printf 'x%.0s' $(seq 1 5000); printf '\\n'"),
            TestContext.Current.CancellationToken);

        var line = Assert.Single(published.Lines("run-4", "stdout"));
        Assert.Equal(2_001, line.Length);
        Assert.EndsWith("x…", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_line_of_emoji_is_not_cut_through_a_surrogate_pair()
    {
        // The cap counts Unicode scalars, as 1.7.2's chars().take() does. Counting UTF-16
        // units instead would split a pair and emit a lone surrogate; and a line of pairs is twice as
        // long in units as in scalars, so it reaches the slow path and can run out of characters
        // before the cap — which is where the bounds check earns its place.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        await registry.RunAsync(
            new AiRunContext("run-9"),
            Script("printf '🔥%.0s' $(seq 1 1500); printf '\\n'"),
            TestContext.Current.CancellationToken);

        var line = Assert.Single(published.Lines("run-9", "stdout"));

        // 1,500 scalars is under the 2,000 cap, so nothing is cut and nothing is marked.
        Assert.Equal(1_500, line.EnumerateRunes().Count());
        Assert.DoesNotContain('…', line);
        Assert.All(line.EnumerateRunes(), rune => Assert.Equal(0x1F525, rune.Value));
    }

    [Fact]
    public async Task Blank_lines_never_reach_the_log()
    {
        // The CLIs pad their output generously and the log reads better without the gaps, so
        // emit_line drops anything that is empty once trailing whitespace is off it.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        await registry.RunAsync(
            new AiRunContext("run-5"),
            Script("printf 'one\\n\\n   \\ntwo\\n'"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["one", "two"], published.Lines("run-5", "stdout"));
    }

    [Fact]
    public async Task A_progress_bar_rewritten_with_carriage_returns_stays_one_line()
    {
        // StreamReader.ReadLine would break on a bare \r and turn one progress bar into three
        // lines; 1.7.2 splits on \n alone. It would also re-join the captured output with
        // the platform newline, which is what the second assertion guards.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        var outcome = await registry.RunAsync(
            new AiRunContext("run-6"),
            Script("printf '10%%\\r50%%\\r100%%\\ndone\\n'"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["10%\r50%\r100%", "done"], published.Lines("run-6", "stdout"));
        Assert.Equal("10%\r50%\r100%\ndone\n", outcome.StandardOutput);
    }

    [Fact]
    public async Task The_trace_keeps_the_tail_of_a_chatty_run()
    {
        // Bounded so one chatty run cannot bloat the database, and the oldest lines go first: the
        // tail is what explains how a turn ended up where it did.
        var (publish, _) = Recorder.Create();
        var registry = new AiRunRegistry(publish);
        var run = new AiRunContext("run-7");

        await registry.RunAsync(
            run,
            Script("for i in $(seq 1 350); do printf 'line %d\\n' \"$i\"; done"),
            TestContext.Current.CancellationToken);

        Assert.Equal(300, run.Trace.Count);
        Assert.Equal("line 51", run.Trace[0].Line);
        Assert.Equal("line 350", run.Trace[^1].Line);
        Assert.All(run.Trace, line => Assert.Equal("stdout", line.Stream));
    }

    [Fact]
    public async Task An_untracked_run_is_captured_but_never_published()
    {
        // What keeps the internal auxiliary calls — model listings, version probes — out of the
        // user's activity log and out of the stop-button registry.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        var outcome = await registry.RunAsync(
            run: null,
            Script("printf 'quiet\\n'"),
            TestContext.Current.CancellationToken);

        Assert.Equal("quiet\n", outcome.StandardOutput);
        Assert.Empty(published.EventNames());
    }

    [Fact]
    public async Task Stdin_is_piped_in_and_closed_so_the_child_sees_eof()
    {
        // An engine that reads stdin needs EOF before it produces anything; `cat` would hang
        // forever if the handle were left open.
        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        await registry.RunAsync(
            new AiRunContext("run-8"),
            Script("cat"),
            async (stdin, token) =>
            {
                await stdin.WriteAsync("payload\n".AsMemory(), token);
                stdin.Close();
                return true;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["payload"], published.Lines("run-8", "stdout"));
    }

    /// <summary>
    /// The environment variable that opts a run into calling the real CLI.
    /// </summary>
    /// <remarks>
    /// <b>Opt-in, because this test spends money and cannot be made reliable.</b> It invokes
    /// <c>claude -p</c> against the developer's own account, so an unguarded run bills a model call on
    /// every <c>dotnet test</c>; and it depends on the network, the account's rate limit and a
    /// two-minute budget, so it turns a 7-second suite into an occasionally-failing two-minute one.
    /// Both were observed. Its value is real — it is the only thing that would catch the CLI rejecting
    /// a flag — so it is kept and gated rather than deleted: run
    /// <c>CODEFLOW_TEST_REAL_CLI=1 dotnet test</c> deliberately, e.g. after an upgrade of the CLI.
    /// </remarks>
    private const string RealCliOptIn = "CODEFLOW_TEST_REAL_CLI";

    /// <summary>
    /// Runs the real CLI, which is the only way to know its flags are still accepted.
    /// </summary>
    /// <remarks>
    /// Skipped unless <see cref="RealCliOptIn"/> is set, and skipped when <c>claude</c> is not
    /// installed. It needs a working account, so it asserts the
    /// mechanics — a terminal <c>result</c> event arrived and interpretation read it — rather than
    /// anything about the reply's content.
    /// </remarks>
    [Fact]
    public async Task The_real_cli_streams_events_and_ends_with_a_result()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(RealCliOptIn) is { Length: > 0 },
            $"set {RealCliOptIn}=1 to run this — it calls the real CLI and bills a model call");

        var binary = Which("claude");
        Assert.SkipWhen(binary is null, "the claude CLI is not installed on this machine");

        var (publish, published) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));

        var command = new Claude().BuildCommand(binary!, new AiInvocation("Reply with exactly: pong", StdinContent: ""));
        var outcome = await registry.RunAsync(new AiRunContext("real-1"), command, timeout.Token);

        var lines = published.Lines("real-1", "stdout");
        Assert.NotEmpty(lines);

        // stream-json emits one event per line as the run happens; a single blob would mean the
        // flags were silently ignored and there would be nothing to show in the log.
        Assert.True(lines.Count > 1, $"expected several streamed events, got {lines.Count}");
        Assert.Contains(lines, l => l.Contains("\"type\":\"system\"", StringComparison.Ordinal));

        var run = new Claude().Interpret(
            outcome.Success, $"exit status: {outcome.ExitCode}", outcome.StandardOutput, outcome.StandardError);
        Assert.False(string.IsNullOrWhiteSpace(run.Text));
    }

    /// <summary>
    /// Runs a POSIX shell script, on whichever shell the platform has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These scripts are the test's subject matter — what is being checked is how the streaming
    /// reader handles what a child process prints, so a real child that really prints is the point.
    /// Rewriting them per platform would mean two sets of fixtures asserting two different things.
    /// </para>
    /// <para>
    /// <c>/bin/sh</c> does not exist on Windows, which is why eleven of these failed the first time
    /// CI ever ran there. The shell used instead is the <c>bash</c> that ships with Git — the same
    /// one <c>ShellResolver</c> picks for a Windows terminal session, and one every GitHub runner
    /// has. A Windows machine without Git skips rather than fails: the reader is platform-independent
    /// and there is nothing here that only a missing shell could prove.
    /// </para>
    /// </remarks>
    private static ProcessStartInfo Script(string script)
    {
        var info = new ProcessStartInfo { FileName = PosixShell() };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        return info;
    }

    private static string PosixShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "/bin/sh";
        }

        var bash = Which("bash.exe") ?? GitBashOrNull();
        Assert.SkipWhen(bash is null, "no POSIX shell on this Windows machine; Git for Windows ships one.");

        return bash!;
    }

    /// <summary>Where Git for Windows' installer puts bash, 64-bit first.</summary>
    private static readonly string[] GitBashPaths =
    [
        @"C:\Program Files\Git\bin\bash.exe",
        @"C:\Program Files (x86)\Git\bin\bash.exe",
    ];

    private static string? GitBashOrNull() => GitBashPaths.FirstOrDefault(File.Exists);

    private static string? Which(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return paths.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
    }
}
