using System.Diagnostics;
using System.Globalization;
using CodeFlow.Ai.Engines;
using CodeFlow.Platform;

namespace CodeFlow.Ai;

/// <summary>
/// The one path every headless AI invocation takes.
/// </summary>
/// <remarks>
/// <para>
/// Builds the engine's command, pipes the payload in, streams the output while it runs, and hands
/// the result back to the engine to interpret. Everything provider-neutral lives here so the six
/// adapters stay small: binary resolution, the augmented <c>PATH</c>, stdin plumbing, cancellation,
/// and the quota tag.
/// </para>
/// <para>
/// The two HTTP transports never reach any of that — <c>binary</c> is a base URL for them, one
/// request answers all at once, and there is no process to stream (<c>AI-003</c>). They are handed
/// off before the first subprocess concern.
/// </para>
/// </remarks>
internal static class AiEngineRunner
{
    /// <summary>Binds the runner to the process-wide registry and HTTP client.</summary>
    /// <remarks>
    /// The operations take the resulting delegate, which is what lets a test drive them against a
    /// scripted engine without a subprocess.
    /// </remarks>
    public static AiRunner Bind(AiRunRegistry runs, HttpClient http) =>
        (config, invocation, run, cancellationToken) =>
            RunAsync(config, invocation, run, runs, http, cancellationToken);

    /// <summary>Runs one invocation and returns the engine's reply.</summary>
    /// <exception cref="AiRunFailedException">
    /// The run failed in a way the user should see: the binary would not launch, the CLI reported an
    /// error, or the user stopped it (in which case the message carries
    /// <see cref="AiRunRegistry.CancelledMarker"/>).
    /// </exception>
    public static async Task<AiRun> RunAsync(
        AiConfig config,
        AiInvocation invocation,
        AiRunContext? run,
        AiRunRegistry runs,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var engine = config.Engine;
        var binary = config.BinaryPath;

        try
        {
            return engine.Transport switch
            {
                // The quota tag is applied here for the HTTP transports and nowhere else, exactly as
                // in 1.7.2: the subprocess engines each recognise their own CLI's refusal
                // inside Interpret, and tagging their failures a second time here would turn a
                // generic error whose text happens to mention a limit into a billing notice.
                Transport.Ollama => await Marked(
                    Ollama.CompleteAsync(http, binary, invocation, cancellationToken)).ConfigureAwait(false),

                Transport.OpenAiCompatible openAi => await Marked(
                    OpenAi.CompleteAsync(http, binary, openAi.ApiKey, invocation, cancellationToken))
                    .ConfigureAwait(false),

                _ => await SubprocessAsync(engine, binary, invocation, run, runs, cancellationToken)
                    .ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            // A stopped run is not a failure, and the frontend tells them apart by this prefix
            // alone. Rethrowing the cancellation instead would surface as an unhandled command
            // error and lose that distinction.
            throw new AiRunFailedException(AiRunRegistry.CancelledMarker);
        }
    }

    /// <summary>Tags a failed HTTP completion as a quota refusal when it reads like one.</summary>
    private static async Task<AiRun> Marked(Task<AiRun> completion)
    {
        try
        {
            return await completion.ConfigureAwait(false);
        }
        catch (AiRunFailedException failure)
        {
            throw new AiRunFailedException(QuotaSignals.Mark(failure.Message));
        }
    }

    private static async Task<AiRun> SubprocessAsync(
        IAiEngine engine,
        string binary,
        AiInvocation invocation,
        AiRunContext? run,
        AiRunRegistry runs,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AttemptAsync(engine, binary, invocation, run, runs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AiRunFailedException failure) when (Repeatable(failure, invocation))
        {
            // Once, immediately, and only for a run that was never going to change anything. The
            // CLI died before it reached its own backend, so it did no work and produced no partial
            // output — but this cannot know how far a *write* run got, and repeating one that had
            // already edited files would apply the same change twice. `AutoApproveEdits` is the
            // app's own statement of which runs those are.
            return await AttemptAsync(engine, binary, invocation, run, runs, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Whether a failed attempt may simply be made again.</summary>
    private static bool Repeatable(AiRunFailedException failure, AiInvocation invocation) =>
        !invocation.AutoApproveEdits && TransientNetwork.Matches(failure.Message);

    private static async Task<AiRun> AttemptAsync(
        IAiEngine engine,
        string binary,
        AiInvocation invocation,
        AiRunContext? run,
        AiRunRegistry runs,
        CancellationToken cancellationToken)
    {
        var directories = BinaryDiscovery.SearchDirs();

        var startInfo = engine.BuildCommand(BinaryDiscovery.ResolveBinary(binary, directories), invocation);
        BinaryDiscovery.ApplyPath(startInfo, directories);

        // The scratch files the command references (opencode's --file, agy's brief directory)
        // must outlive the child process and nothing else — deleted in the finally so every
        // exit (reply, CLI error, launch failure, cancellation) cleans up (BUG-AI-a, closed).
        var scratch = EngineScratch.CollectFrom(startInfo);
        var payload = engine.StdinPayload(invocation);
        try
        {
            ProcessOutcome outcome;
            try
            {
                outcome = await runs
                    .RunAsync(run, startInfo, WriteStdin(payload), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new AiRunFailedException($"failed to launch '{binary}': {failure.Message}");
            }

            // The CLI speaks first. When it failed, its own message is the reason — an unknown flag,
            // an expired session, a prompt over the model's limit — and Interpret throws carrying it.
            // A child that dies at startup takes its stdin down with it, so checking the pipe before
            // this would report the plumbing and bury the cause (`AI-048`).
            var result = engine.Interpret(
                outcome.Success,
                StatusLabel(outcome.ExitCode),
                AiText.StripAnsi(outcome.StandardOutput),
                AiText.StripAnsi(outcome.StandardError));

            // It reported success, but it stopped reading before the data finished arriving. What it
            // answered was formed from an unknown fraction of the diff, which makes it an answer to a
            // question nobody asked — worse than no answer, because it looks complete.
            if (payload.Length > 0 && !outcome.StdinDelivered)
            {
                var size = payload.Length.ToString(CultureInfo.InvariantCulture);
                throw new AiRunFailedException(
                    $"{binary} stopped reading its input before all {size} characters had been handed"
                    + " over, so its answer covers an unknown part of the change");
            }

            return result;
        }
        finally
        {
            EngineScratch.TryDelete(scratch);
        }
    }

    /// <summary>
    /// Feeds the payload to stdin and closes it, reporting whether all of it got through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two reasons this is not an inline write before waiting. An engine that <em>ignores</em> stdin
    /// would deadlock once the OS pipe buffer filled, because nothing drains it; here the write
    /// simply fails with a broken pipe once the child exits. And an engine that <em>does</em> read
    /// stdin needs EOF before it starts producing output, which closing the handle sends.
    /// </para>
    /// <para>
    /// <b>Nothing here may throw</b>, and the close needs its own guard to keep that promise:
    /// <see cref="StreamWriter.Close"/> flushes, so a pipe that broke mid-write breaks again on the
    /// way out — from a <c>finally</c>, where the <c>catch</c> above cannot see it. That second
    /// exception escaped as <c>IOException: Pipe is broken.</c> and replaced whatever the run had
    /// produced: a finished review, or the CLI's own account of why it died (<c>AI-048</c>).
    /// </para>
    /// </remarks>
    /// <returns>Whether the whole payload reached the child.</returns>
    private static Func<StreamWriter, CancellationToken, Task<bool>> WriteStdin(string payload) =>
        async (stdin, cancellationToken) =>
        {
            var delivered = false;
            try
            {
                await stdin.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
                delivered = true;
            }
            catch (IOException)
            {
                // The child stopped reading. Whether that matters is the caller's judgement, not
                // this function's: it depends on whether the engine was meant to read stdin at all.
            }
            finally
            {
                try
                {
                    stdin.Close();
                }
                catch (IOException)
                {
                    // Flushing, into the same broken pipe, what the write above could not deliver.
                }
            }

            return delivered;
        };

    /// <summary>
    /// How the exit status is named inside an engine's error message.
    /// </summary>
    /// <remarks>
    /// The format is <c>exit status: N</c>. .NET only exposes the encoded <c>128 + signal</c> exit
    /// code rather than the signal itself, so a process killed by <c>SIGKILL</c> reads as
    /// <c>exit status: 137</c> rather than naming the signal. The string is diagnostic text in an
    /// error the user sees, not something either side parses.
    /// </remarks>
    private static string StatusLabel(int exitCode) =>
        string.Create(CultureInfo.InvariantCulture, $"exit status: {exitCode}");
}
