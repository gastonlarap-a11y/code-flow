using CodeFlow.Activity;
using CodeFlow.Ai;
using CodeFlow.ApiClient;
using CodeFlow.Diagnostics;
using CodeFlow.Files;
using CodeFlow.Git;
using CodeFlow.Ipc;
using CodeFlow.Platform;
using CodeFlow.Providers;
using CodeFlow.Review;
using CodeFlow.Security;
using CodeFlow.Storage;
using CodeFlow.Terminal;
using CodeFlow.Update;
using CodeFlow.Workspaces;

namespace CodeFlow;

/// <summary>
/// Entry point for the CodeFlow sidecar.
/// </summary>
/// <remarks>
/// <para>
/// This file is deliberately thin and must stay that way: composition and transport only. Every
/// command handler body belongs in its feature folder, so that a developer opening the solution
/// finds a feature's code in one place.
/// </para>
/// <para>
/// The startup ordering below is load-bearing and documented in
/// <c>docs/business-rules/02-bootstrap-platform.md</c>. Reordering any of it corrupts an existing
/// user's data, so it stays as written.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--smoke-test", ..])
        {
            return await SmokeTest.RunAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var token = await ReadIpcTokenAsync().ConfigureAwait(false);
        if (token is null)
        {
            await Console.Error.WriteLineAsync(
                "codeflow-core: the IPC token is read from the first line of stdin. This process is spawned by the shell, not run directly.")
                .ConfigureAwait(false);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            return await RunAsync(args, token, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static async Task<int> RunAsync(string[] args, string token, CancellationToken cancellationToken)
    {
        // Steps 1–3 run through `Stage` so a failure in any of them says so somewhere. None of them
        // has a fallback path — if one throws the app does not start — and until this wrapper existed
        // that exit produced no artefact at all: no log, no window, nothing for the user to report.

        // 1. The reset marker is checked before anything opens the base directory. A reset
        //    request cannot delete the database from under a live SQLite connection, so it is
        //    always handled here, on the next launch, when nothing has touched the directory yet.
        Stage("reset-marker", ApplyPendingReset);

        // 2. Directories before any file is opened inside them. The scratch sweep rides along:
        //    engine temp files whose invocation died with a previous process have no owner left,
        //    and startup is the one moment that is provably true of anything old enough
        //    (BUG-AI-a, closed).
        Stage("directories", AppPaths.EnsureDirectories);
        Stage("scratch-sweep", () => Ai.EngineScratch.SweepOrphans(Path.GetTempPath(), DateTime.UtcNow));

        // 3. Storage: open the connection and run every migration to completion. Nothing may
        //    observe a half-migrated schema, which is why this happens before the command
        //    surface exists rather than lazily on first use.
        // A lambda rather than the method group: `Open` has an optional parameter, which a method
        // group conversion does not fill in.
        await using var database = Stage("storage", () => Database.Open());

        // 4. The transport and the state that needs to push events through it.
        //
        //    The registry is created empty and populated below rather than being handed in
        //    complete. Terminal sessions publish events through the server, and the server
        //    dispatches through the registry, so one of the three has to be wired after
        //    construction — doing it here keeps that fact visible instead of hiding it behind an
        //    interface with a single implementation.
        var registry = new CommandRegistry();

        var endpoint = ArgValue(args, "--ipc-endpoint") ?? AppPaths.IpcEndpoint(Environment.ProcessId);
        await using var listener = IpcListener.Create(endpoint);
        await using var server = new IpcServer(registry, token, ErrorLog.Record);
        await using var terminals = new TerminalRegistry(server.PublishAsync);
        await using var watcher = new RepoWatcher(server.PublishAsync);
        using var apiRequests = new ApiRegistry();
        await using var apiStreams = new StreamRegistry(server.PublishAsync);
        var aiRuns = new AiRunRegistry(server.PublishAsync);
        var gitNetwork = new GitNetwork(server.PublishAsync);

        // One client for the process, as HttpClient wants: the two HTTP-transport AI providers are
        // the only callers today, and both are pointed at a host the user configures.
        // PooledConnectionLifetime bounds how long a pooled connection may be reused, so a DNS
        // change on that host is picked up within 15 minutes instead of never (.NET HttpClient
        // guidelines for a process-lifetime client).
        using var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        // 5. The command surface, contributed per feature. Sealed before the transport accepts a
        //    connection, so no handler can be added once dispatch is live.
        registry
            .AddAppCommands()
            .AddWorkspaceCommands(database)
            .AddSkillCommands(database, new SkillInstaller(server.PublishAsync))
            .AddSecretCommands()
            .AddTerminalCommands(terminals)
            .AddAiCommands(aiRuns, database, http)
            .AddActivityCommands(database)
            .AddProviderCommands(database, aiRuns, http)
            .AddReviewCommands(database, aiRuns, http, gitNetwork)
            .AddGitCommands(gitNetwork, database)
            .AddFileCommands()
            .AddWatcherCommands(watcher)
            .AddApiCommands(database)
            .AddApiHttpCommands(apiRequests)
            .AddApiStreamCommands(apiStreams)
            // The version comes from the shell, which is the only side that knows it: it lives in
            // `shell/package.json` and reaches Electron through `app.getVersion()`. Hard-coding it
            // here would mean two places to bump and one of them silently wrong.
            .AddUpdateCommands(http, server.PublishAsync, ArgValue(args, "--app-version") ?? "0.0.0")
            .Seal();

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        server.ChannelClosed += (_, kind) =>
        {
            // The shell is the only client. If its RPC channel drops, this process has nobody to
            // serve and nothing to keep alive — and staying up would leak whatever it is running
            // on the shell's behalf. A single-process app got this for free; running the core
            // as a separate process is what makes it explicit work.
            if (kind == IpcChannelKind.Rpc)
            {
                lifetime.Cancel();
            }
        };

        // The shell reads this line to know the endpoint is listening. It is the one thing that
        // crosses stdout: control traffic goes over the pipe, and this is a readiness signal
        // emitted once, before any framing exists to carry it.
        Console.WriteLine($"codeflow-core ready {endpoint}");
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);

        await server.RunAsync(listener, lifetime.Token).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Runs one startup step, recording the failure before letting it end the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exception is rethrown untouched: nothing here is recoverable, and the ordering these steps
    /// depend on means continuing past a failed one would corrupt what the next one assumes
    /// (<c>BOOT-001</c>, <c>BOOT-002</c>). What changes is only that it is written down first.
    /// </para>
    /// <para>
    /// The stage name is the diagnostic: "directories" and "storage" fail for entirely different
    /// reasons — a permission on the data directory versus a migration against an older schema — and
    /// a bare stack trace does not distinguish them at a glance.
    /// </para>
    /// </remarks>
    private static T Stage<T>(string stage, Func<T> work)
    {
        try
        {
            return work();
        }
        catch (Exception failure)
        {
            StartupLog.Record(stage, failure);
            throw;
        }
    }

    /// <inheritdoc cref="Stage{T}(string, Func{T})"/>
    private static void Stage(string stage, Action work) =>
        Stage(stage, () =>
        {
            work();
            return 0;
        });

    /// <summary>Honours a reset requested by the previous run.</summary>
    /// <remarks>
    /// Deliberately wipes the base directory only. The OS keychain is untouched, matching the
    /// Windows uninstaller's identical scope (<c>DIVERGENCE-BOOT-b</c>). Purging credentials here
    /// would destroy data the user never asked to lose.
    /// </remarks>
    private static void ApplyPendingReset()
    {
        if (!File.Exists(AppPaths.ResetMarkerFile))
        {
            return;
        }

        try
        {
            Directory.Delete(AppPaths.BaseDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // CodeFlow 1.7.2 discards this failure too. A reset that cannot complete must not stop
            // the app from starting, or a locked file would leave the user with nothing that runs.
        }
    }

    /// <summary>
    /// Reads the IPC handshake token from the first line of stdin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to arrive as <c>--ipc-token &lt;uuid&gt;</c>. A command line is world-readable on
    /// POSIX — any process the same user runs can recover it from <c>ps</c> or
    /// <c>/proc/&lt;pid&gt;/cmdline</c> — which made the token a secret stored in the one place
    /// designed to be public. An environment variable would be narrower but not narrow enough:
    /// this process spawns AI CLIs, <c>git</c> and <c>npx</c>, and they inherit its environment.
    /// </para>
    /// <para>
    /// stdin leaves nothing behind. The shell writes one line and closes the pipe; after that
    /// there is no artefact another process could read. The socket permissions
    /// (<c>IpcListener</c>) remain the first line of defence — this is what stops the second one
    /// from being handed out for free.
    /// </para>
    /// </remarks>
    private static async Task<string?> ReadIpcTokenAsync()
    {
        var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
