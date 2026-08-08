using System.Diagnostics;
using System.Globalization;
using CodeFlow.Ai;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// What happens when a CLI stops reading the payload it was being handed.
/// </summary>
/// <remarks>
/// <para>
/// The bug these fix, <c>AI-048</c>: the writer swallowed the broken pipe from its <em>write</em>
/// and then closed the handle in a <c>finally</c>, where <see cref="StreamWriter.Close"/> flushed
/// the rest into the same broken pipe and threw again, outside the <c>catch</c>. That second
/// exception surfaced as <c>IOException: Pipe is broken.</c> — and because the stdin task is awaited
/// after the process has already exited, it replaced whatever the run had produced: a whole review,
/// or the CLI's own account of why it died at startup.
/// </para>
/// <para>
/// Against a real subprocess, because that is the only place the pipe exists. The payload has to
/// outgrow the OS pipe buffer (64 KiB on both platforms here) or the kernel would swallow the whole
/// thing and nobody would notice the child never read it.
/// </para>
/// </remarks>
public sealed class StdinDeliveryTests
{
    private const int LargerThanAnyPipeBuffer = 512 * 1024;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact]
    public async Task A_child_that_never_reads_its_input_leaves_the_run_intact()
    {
        var (publish, _) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        var outcome = await registry.RunAsync(
            run: null,
            Script("printf 'the answer\\n'"),
            Writer(new string('x', LargerThanAnyPipeBuffer)),
            TestContext.Current.CancellationToken);

        // The point of the test: the pipe broke, and the run survived it with its output.
        Assert.False(outcome.StdinDelivered);
        Assert.True(outcome.Success);
        Assert.Equal("the answer\n", outcome.StandardOutput);
    }

    [Fact]
    public async Task A_child_that_drains_its_input_reports_a_complete_delivery()
    {
        var (publish, _) = Recorder.Create();
        var registry = new AiRunRegistry(publish);

        var outcome = await registry.RunAsync(
            run: null,
            Script("wc -c"),
            Writer(new string('x', LargerThanAnyPipeBuffer)),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.StdinDelivered);
        Assert.Equal(LargerThanAnyPipeBuffer, int.Parse(outcome.StandardOutput.Trim(), Culture));
    }

    [Fact]
    public async Task An_engine_fed_on_stdin_refuses_an_answer_formed_from_part_of_it()
    {
        var registry = new AiRunRegistry(Recorder.Create().Publish);
        var engine = new ShellEngine("printf 'a review of something\\n'", readsStdin: true);
        using var http = new HttpClient();

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiEngineRunner.RunAsync(
            new AiConfig(engine, engine.Id, string.Empty, engine.DefaultBinary, null),
            Invocation(new string('x', LargerThanAnyPipeBuffer)),
            run: null,
            registry,
            http,
            TestContext.Current.CancellationToken));

        // Not "Pipe is broken." — the message says what actually went wrong, and names the size so
        // the next occurrence is diagnosable rather than mysterious.
        Assert.Contains("stopped reading its input", failure.Message, StringComparison.Ordinal);
        Assert.Contains(LargerThanAnyPipeBuffer.ToString(Culture), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_engine_that_does_not_use_stdin_is_unbothered_by_the_pipe_it_left_behind()
    {
        var registry = new AiRunRegistry(Recorder.Create().Publish);
        var engine = new ShellEngine("printf 'a review of something\\n'", readsStdin: false);
        using var http = new HttpClient();

        var result = await AiEngineRunner.RunAsync(
            new AiConfig(engine, engine.Id, string.Empty, engine.DefaultBinary, null),
            Invocation(new string('x', LargerThanAnyPipeBuffer)),
            run: null,
            registry,
            http,
            TestContext.Current.CancellationToken);

        Assert.Equal("a review of something", result.Text);
    }

    private static AiInvocation Invocation(string data) => new("review this", data);

    /// <summary>A writer shaped like the runner's own: it may not throw, and it reports delivery.</summary>
    private static Func<StreamWriter, CancellationToken, Task<bool>> Writer(string payload) =>
        async (stdin, token) =>
        {
            var delivered = false;
            try
            {
                await stdin.WriteAsync(payload.AsMemory(), token);
                await stdin.FlushAsync(token);
                delivered = true;
            }
            catch (IOException)
            {
                // The child stopped reading.
            }
            finally
            {
                try
                {
                    stdin.Close();
                }
                catch (IOException)
                {
                    // The close flushes, into the same broken pipe.
                }
            }

            return delivered;
        };

    private static ProcessStartInfo Script(string script)
    {
        var info = new ProcessStartInfo { FileName = PosixShell() };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        return info;
    }

    private static string PosixShell()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the pipe semantics under test are POSIX; the shell script is too.");
        return "/bin/sh";
    }

    /// <summary>An engine that runs a shell script, so the runner's own plumbing is what is tested.</summary>
    private sealed class ShellEngine(string script, bool readsStdin) : IAiEngine
    {
        public string Id => "shell";

        public string Label => "Shell";

        public string DefaultBinary => PosixShell();

        public string StdinPayload(AiInvocation invocation) =>
            readsStdin ? invocation.StdinContent : string.Empty;

        public ProcessStartInfo BuildCommand(string binary, AiInvocation invocation) => Script(script);

        public AiRun Interpret(bool success, string statusLabel, string stdout, string stderr) =>
            success
                ? new AiRun(stdout.Trim(), null, null)
                : throw new AiRunFailedException($"the script failed ({statusLabel}): {stderr}");
    }
}
