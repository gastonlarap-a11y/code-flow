using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Ipc;

namespace CodeFlow.Ai;

/// <summary>One line an engine printed while a run was in flight.</summary>
/// <param name="RunId">The run this line belongs to; every window receives every event.</param>
/// <param name="Stream">
/// <c>stdout</c> or <c>stderr</c>. The UI dims the latter, because most CLIs use it for progress
/// chatter rather than for failures.
/// </param>
public sealed record AiOutputEvent(string RunId, string Stream, string Line);

/// <summary>
/// Runs an engine subprocess, streams its output, and lets a run be cancelled mid-flight.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>ai:output</c> carries an activity log, not the answer.</b> The reply is extracted from
/// the terminal <c>result</c> event once the process exits. This distinction matters and is easy
/// to get backwards: <c>.claude/rules/dotnet.md</c> asks the spike to prove events "rendering
/// incrementally in the UI", which the app does not do — proving that would prove a feature §9
/// forbids adding. <c>docs/business-rules/90-ambiguities.md</c> restates the real behaviour, and this is it.
/// </para>
/// <para>
/// Cancellation needs nothing from the transport: the frontend calls <c>cancel_ai_run(runId)</c>
/// like any other command, and this registry maps the id to a
/// <see cref="CancellationTokenSource"/>. Killing the process tree matters — on Windows the
/// immediate child is usually a <c>.cmd</c> shim, so signalling only it leaves the real CLI
/// running and billing.
/// </para>
/// </remarks>
/// <param name="publish">Where an <c>ai:output</c> line goes.</param>
/// <param name="runTimeout">
/// Overrides <see cref="DefaultRunTimeout"/>. A seam for the tests, which cannot wait ten minutes
/// to prove that a run that never ends is cut short — production composition passes nothing.
/// </param>
public sealed class AiRunRegistry(PublishEvent publish, TimeSpan? runTimeout = null)
{
    /// <summary>
    /// A single emitted line is capped before it crosses the IPC boundary.
    /// </summary>
    /// <remarks>
    /// A CLI drawing a progress bar can produce megabytes on one "line", and the UI only ever
    /// shows a tail anyway.
    /// </remarks>
    private const int MaxLineChars = 2_000;

    /// <summary>
    /// How much output is buffered before it is emitted as a line even without a newline.
    /// </summary>
    /// <remarks>
    /// A CLI drawing a progress bar rewrites one line forever with <c>\r</c> and never sends a
    /// newline; without this the buffer would grow unbounded and the user would see nothing. The
    /// reference counts bytes here and this counts chars — the threshold is a safety valve, not a
    /// contract, so the difference only shifts where a pathological line is cut.
    /// </remarks>
    private const int MaxPendingChars = 8_192;

    /// <summary>
    /// Prefix on the error of a run the user stopped, so the frontend renders "cancelled" instead of
    /// a red failure banner. `VERBATIM`.
    /// </summary>
    /// <remarks>
    /// Read by <c>aiRunStore.ts</c>'s <c>isCancellation</c>. A cancelled turn is also the one
    /// outcome that is never persisted, so this string is what keeps a deliberate stop out of the
    /// transcript — see <c>docs/business-rules/05-ai-engines.md</c>, <c>AI-050</c>.
    /// </remarks>
    public const string CancelledMarker = "RUN_CANCELLED::";

    /// <summary>
    /// Prefix on the error of a run that outlived its deadline, so the frontend can say
    /// the run was cut short rather than blame the user for a stop they never pressed. `VERBATIM`.
    /// </summary>
    /// <remarks>
    /// Read by <c>aiRunStore.ts</c>'s <c>isTimeout</c>; see
    /// <c>docs/business-rules/13-cross-language-contracts.md</c>, <c>XLANG-003</c>.
    /// </remarks>
    public const string TimedOutMarker = "RUN_TIMED_OUT::";

    /// <summary>
    /// How long one agent invocation may run before its process tree is killed.
    /// </summary>
    /// <remarks>
    /// Nothing else bounded a run: the wait was linked only to the caller's token, so a CLI that
    /// never exits left the panel spinning forever with the stop button — inside a collapsed log —
    /// as the only way out. A child does not have to be broken to reach this: the CLI runs with the
    /// analysed repository as its working directory, so that repository's own turn hooks run inside
    /// the review, and a hook that type-checks and lints holds the process open for minutes
    /// (<c>AI-013</c>). Generous on purpose — a large PR review legitimately takes a while, and this
    /// is a backstop, not a budget.
    /// </remarks>
    public static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long the pipe readers get to notice a killed child before the run reports back.
    /// </summary>
    /// <remarks>
    /// Bounded rather than awaited outright: a pipe read is not reliably interruptible on Unix, and
    /// the stop button must not wait on one. Whatever is still reading finishes on its own with its
    /// failure observed.
    /// </remarks>
    private static readonly TimeSpan PumpDrainGrace = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _runTimeout = runTimeout ?? DefaultRunTimeout;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();

    /// <summary>Requests cancellation. Returns whether a run with that id was in flight.</summary>
    /// <remarks>
    /// "Requested", not "confirmed stopped": 1.7.2 returns as soon as the signal is sent
    /// rather than waiting for the subprocess to die, and making the caller wait here would stall
    /// the UI on process cleanup it never used to wait for.
    /// </remarks>
    public bool Cancel(string runId)
    {
        if (!_runs.TryGetValue(runId, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended on its own between the lookup and here — `RunAsync` removes the entry
            // and disposes the source on its way out. There is nothing left to stop, which is the
            // same answer as an id that was never in flight.
            return false;
        }

        return true;
    }

    /// <summary>
    /// Runs a process, publishing and recording every line it prints, and returns its raw output.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> <paramref name="run"/> is an untracked run: the process is spawned
    /// and captured, but nothing is published and there is no stop handle. Output comes back
    /// unmodified — ANSI stripping belongs to the caller that hands it to
    /// <see cref="IAiEngine.Interpret"/>.
    /// </remarks>
    public Task<ProcessOutcome> RunAsync(
        AiRunContext? run,
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken) =>
        RunAsync(run, startInfo, writeStdin: null, cancellationToken);

    /// <inheritdoc cref="RunAsync(AiRunContext?, ProcessStartInfo, CancellationToken)"/>
    /// <param name="writeStdin">
    /// Fills the child's stdin, concurrently with the output being drained, and is responsible for
    /// closing it. Returns whether all of the payload got through, and <b>must not throw</b>: it is
    /// awaited after the process has already exited, so an exception from it would discard a run
    /// that finished. <see langword="null"/> leaves stdin unredirected.
    /// </param>
    public async Task<ProcessOutcome> RunAsync(
        AiRunContext? run,
        ProcessStartInfo startInfo,
        Func<StreamWriter, CancellationToken, Task<bool>>? writeStdin,
        CancellationToken cancellationToken)
    {
        // Two sources, not one: `lifetime` is what a stop cancels, `timeout` is what expiry
        // cancels, and the catch below tells the two apart by asking which one fired. Linked into
        // one token so the wait and both pipe readers observe either.
        using var timeout = new CancellationTokenSource(_runTimeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        // Registered before the spawn so a stop arriving while the process is still starting is
        // still observed.
        if (run is not null)
        {
            _runs[run.RunId] = lifetime;
        }

        startInfo.RedirectStandardInput = writeStdin is not null;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start {startInfo.FileName}");

        // Both pipes are drained concurrently with the wait: reading them only after the process
        // exits would deadlock any CLI whose output outgrows the OS pipe buffer, and there would
        // be nothing to stream in the meantime. Declared out here so the cancellation path can
        // wait on them too — they read from streams the `using` above is about to close.
        var stdout = PumpAsync(process.StandardOutput, run, "stdout", lifetime.Token);
        var stderr = PumpAsync(process.StandardError, run, "stderr", lifetime.Token);

        try
        {
            var stdin = writeStdin is null
                ? Task.FromResult(true)
                : writeStdin(process.StandardInput, lifetime.Token);

            await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            var delivered = await stdin.ConfigureAwait(false);

            return new ProcessOutcome(
                process.ExitCode == 0,
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false),
                delivered);
        }
        catch (OperationCanceledException)
        {
            // The whole tree, not just the immediate child: on Windows that child is usually a
            // .cmd shim, and killing it alone leaves the real CLI running.
            TryKillTree(process);
            await QuiesceAsync(stdout, stderr).ConfigureAwait(false);

            // Expiry, not a stop the user pressed. Checked against the caller's own token so a
            // shutdown that happens to race the deadline still reads as a cancellation.
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new AiRunFailedException(TimedOutMarker + FormatTimeout(_runTimeout));
            }

            throw;
        }
        finally
        {
            if (run is not null)
            {
                _runs.TryRemove(run.RunId, out _);
            }
        }
    }

    /// <summary>
    /// Drains one pipe, emitting complete lines as they arrive and returning everything read.
    /// </summary>
    /// <remarks>
    /// Splits on <c>\n</c> only, and returns the text exactly as the process wrote it. Both matter:
    /// <see cref="StreamReader.ReadLineAsync()"/> would also break on a bare <c>\r</c> — turning one
    /// progress bar into thousands of lines — and would re-join the captured output with the
    /// platform's newline, so on Windows an engine parsing its own output would see line endings the
    /// CLI never wrote.
    /// </remarks>
    private async Task<string> PumpAsync(
        StreamReader reader, AiRunContext? run, string stream, CancellationToken cancellationToken)
    {
        var captured = new StringBuilder();
        var pending = new StringBuilder();
        var buffer = new char[MaxPendingChars];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            captured.Append(buffer, 0, read);

            if (run is null)
            {
                continue;
            }

            pending.Append(buffer, 0, read);

            for (var newline = IndexOfNewline(pending); newline >= 0; newline = IndexOfNewline(pending))
            {
                await EmitAsync(run, stream, pending.ToString(0, newline), cancellationToken).ConfigureAwait(false);
                pending.Remove(0, newline + 1);
            }

            if (pending.Length > MaxPendingChars)
            {
                await EmitAsync(run, stream, pending.ToString(), cancellationToken).ConfigureAwait(false);
                pending.Clear();
            }
        }

        // Whatever the process wrote without a final newline is still a line.
        if (run is not null && pending.Length > 0)
        {
            await EmitAsync(run, stream, pending.ToString(), cancellationToken).ConfigureAwait(false);
        }

        return captured.ToString();
    }

    /// <summary>Records and publishes one line, or drops it when there is nothing left to show.</summary>
    /// <remarks>
    /// Blank lines are dropped — the CLIs pad their output generously and the log reads better
    /// without the gaps — and the trace keeps exactly what was emitted, so a reopened conversation
    /// shows the same text the live log did.
    /// </remarks>
    private async Task EmitAsync(
        AiRunContext run, string stream, string raw, CancellationToken cancellationToken)
    {
        var line = Cap(AiText.StripAnsi(raw).TrimEnd());
        if (line.Length == 0)
        {
            return;
        }

        run.Record(stream, line);

        using var payload = JsonSerializer.SerializeToDocument(
            new AiOutputEvent(run.RunId, stream, line), AiJsonContext.Default.AiOutputEvent);

        await publish("ai:output", payload.RootElement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Truncates to <see cref="MaxLineChars"/> Unicode scalars, marking the cut.</summary>
    /// <remarks>
    /// Counted in scalars, not UTF-16 units, so a line of emoji is not cut through a surrogate pair.
    /// A string whose UTF-16 length fits cannot have more scalars than that, which is the fast path.
    /// </remarks>
    private static string Cap(string line)
    {
        if (line.Length <= MaxLineChars)
        {
            return line;
        }

        // The bound on `index` is not redundant with the fast path above: a line of surrogate pairs
        // has twice the UTF-16 length of its scalar count, so it can reach this loop and still run
        // out of characters before the cap — without the guard, that reads past the end.
        var index = 0;
        for (var scalars = 0; scalars < MaxLineChars && index < line.Length; scalars++)
        {
            index += char.IsHighSurrogate(line[index]) && index + 1 < line.Length ? 2 : 1;
        }

        return index >= line.Length ? line : string.Concat(line.AsSpan(0, index), "…");
    }

    private static int IndexOfNewline(StringBuilder pending)
    {
        for (var i = 0; i < pending.Length; i++)
        {
            if (pending[i] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Kills the child and everything it started, and never throws.
    /// </summary>
    /// <remarks>
    /// The exception net is wide on purpose. <see cref="Process.Kill(bool)"/> reports a descendant
    /// it could not reach as an <see cref="AggregateException"/> and a platform refusal as a
    /// <see cref="System.ComponentModel.Win32Exception"/>, and the <c>HasExited</c> check is a
    /// race, not a guard — the child can exit inside it. Any of those escaping would replace the
    /// <see cref="OperationCanceledException"/> the caller is propagating, and a stop the user
    /// pressed would surface as a red failure instead of "stopped". The tree is deepest exactly
    /// when that matters: a CLI running the repository's own turn hooks is a CLI with a shell, a
    /// package manager and a compiler underneath it.
    /// </remarks>
    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Already gone, unreachable, or the platform refused. Nothing more to do either way,
            // and nothing here is worth losing the cancellation over.
        }
    }

    /// <summary>
    /// Gives the pipe readers a moment to notice the child is gone, and observes their failures.
    /// </summary>
    /// <remarks>
    /// They read from streams that <see cref="Process"/>'s disposal is about to close, so they fail
    /// rather than finish, and an unawaited faulted task is an unobserved exception. The wait is
    /// bounded by <see cref="PumpDrainGrace"/> because a pipe read is not reliably interruptible on
    /// Unix and stopping a run must stay instant; whatever is still reading is left to end on its
    /// own with its failure already observed.
    /// </remarks>
    private static async Task QuiesceAsync(Task<string> stdout, Task<string> stderr)
    {
        var pumps = Task.WhenAll(stdout, stderr);

        _ = pumps.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await Task.WhenAny(pumps, Task.Delay(PumpDrainGrace, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>
    /// How the deadline is named in the message the user reads: whole minutes, or nothing at all.
    /// </summary>
    /// <remarks>
    /// A deadline under a minute is only ever a test's, and "cut short after 0 minutes" reads as a
    /// bug. The renderer falls back to wording that names no duration when this is empty.
    /// </remarks>
    private static string FormatTimeout(TimeSpan limit) =>
        limit < TimeSpan.FromMinutes(1)
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{(int)limit.TotalMinutes}");
}

/// <summary>The raw result of a finished subprocess, before an engine interprets it.</summary>
/// <param name="StdinDelivered">
/// Whether the whole stdin payload reached the child. False means it stopped reading — the pipe
/// broke — so anything it printed was formed from part of the input, not all of it. Defaulted to
/// true so a run with nothing to deliver, and every test that scripts an outcome, reads as complete.
/// </param>
public sealed record ProcessOutcome(
    bool Success,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool StdinDelivered = true);

/// <remarks>
/// snake_case, not camelCase — arguments travel camelCase, payloads travel snake_case (the wire
/// contract <see cref="Git.GitJsonContext"/> documents).
/// <c>events.ts:32</c> reads <c>run_id</c>; emitting <c>runId</c> leaves it undefined and
/// the UI cannot route the line to its run.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AiOutputEvent))]
[JsonSerializable(typeof(ProviderStatus))]
[JsonSerializable(typeof(AiTurn.ChatReply))]
[JsonSerializable(typeof(IReadOnlyList<TraceLine>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
internal sealed partial class AiJsonContext : JsonSerializerContext;
