using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CodeFlow.Ipc;

namespace CodeFlow.Files;

/// <summary>
/// One native watcher per currently-open repo (<c>FILE-012</c>/<c>FILE-013</c>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FileSystemWatcher"/> is the direct counterpart of 1.7.2's <c>notify</c>: both
/// sit on FSEvents on macOS, inotify on Linux and <c>ReadDirectoryChangesW</c> on Windows.
/// </para>
/// <para>
/// The renderer starts one watch per active project and stops it when the project changes, and it
/// filters <c>repo:fs-changed</c> by <c>repo_path</c> itself — the event is broadcast, not routed.
/// </para>
/// </remarks>
public sealed class RepoWatcher(PublishEvent publish) : IAsyncDisposable
{
    /// <summary>How long the loop waits for an event before looking at the pending flag again.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>The minimum gap between two emissions.</summary>
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(400);

    private readonly ConcurrentDictionary<string, Watch> _watches = new(StringComparer.Ordinal);

    /// <summary>Starts watching a repository, replacing any watch already running on it.</summary>
    /// <remarks>
    /// Stopping first is what makes a second call idempotent rather than leaving two watchers alive
    /// — the renderer's effect can re-run for the same project.
    /// </remarks>
    public async ValueTask StartAsync(string repoPath, CancellationToken cancellationToken)
    {
        await StopAsync(repoPath).ConfigureAwait(false);

        var watch = new Watch(repoPath, publish, cancellationToken);

        // A path that vanished between the renderer deciding to watch it and this running is the
        // caller's problem to hear about, not something to swallow.
        if (!_watches.TryAdd(repoPath, watch))
        {
            await watch.DisposeAsync().ConfigureAwait(false);

            return;
        }

        watch.Start();
    }

    /// <summary>Stops watching a repository. Not an error if nothing was watching it.</summary>
    public async ValueTask StopAsync(string repoPath)
    {
        if (_watches.TryRemove(repoPath, out var watch))
        {
            await watch.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>How many watches are live, so a test can prove a second start replaced the first.</summary>
    internal int Count => _watches.Count;

    public async ValueTask DisposeAsync()
    {
        foreach (var path in _watches.Keys)
        {
            await StopAsync(path).ConfigureAwait(false);
        }
    }

    /// <summary>One repository's watcher and the loop that throttles what it reports.</summary>
    private sealed class Watch : IAsyncDisposable
    {
        private readonly string _repoPath;
        private readonly PublishEvent _publish;
        private readonly FileSystemWatcher _watcher;
        private readonly CancellationTokenSource _stopping;

        /// <summary>
        /// Carries whether an event was noise; the decision is made in the loop, as 1.7.2
        /// makes it, so an error from the watcher can bypass it.
        /// </summary>
        private readonly Channel<bool> _events = Channel.CreateUnbounded<bool>(
            new UnboundedChannelOptions { SingleReader = true });

        private Task _loop = Task.CompletedTask;

        public Watch(string repoPath, PublishEvent publish, CancellationToken cancellationToken)
        {
            _repoPath = repoPath;
            _publish = publish;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _watcher = new FileSystemWatcher(repoPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
            };

            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
        }

        public void Start()
        {
            _watcher.EnableRaisingEvents = true;
            _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        }

        private void OnChanged(object sender, FileSystemEventArgs e) => Post(IsNoise(e.FullPath));

        private void OnRenamed(object sender, RenamedEventArgs e) =>
            // A rename carries two paths, and 1.7.2 calls an event noise if *any* of its
            // paths is.
            Post(IsNoise(e.FullPath) || IsNoise(e.OldFullPath));

        private void OnError(object sender, ErrorEventArgs e) =>
            // A buffer overflow — too many changes at once for the OS to describe — is treated the
            // same as a real change rather than silently ignored: we do not know what changed, so
            // the safe move is to refresh.
            Post(isNoise: false);

        private void Post(bool isNoise) => _events.Writer.TryWrite(isNoise);

        /// <summary>Whether a path is git's own bookkeeping rather than the user's work.</summary>
        private static bool IsNoise(string path)
        {
            var name = Path.GetFileName(path);

            return name.EndsWith(".lock", StringComparison.Ordinal)
                || name == "FETCH_HEAD"
                || name == "COMMIT_EDITMSG";
        }

        /// <summary>
        /// A leading-edge throttle with a trailing catch-up (<c>DIVERGENCE-FILE-b</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// 1.7.2's own comment, on why this is deliberately <b>not</b> a plain debounce:
        /// </para>
        /// <para>
        /// "the first event of a burst emits immediately; anything else within 400ms just marks a
        /// change as pending instead of being dropped outright. Once the burst goes quiet, the next
        /// poll tick (at most ~200ms later, and only once 400ms has actually elapsed since the last
        /// emit) flushes that pending change — a plain leading-edge throttle
        /// (emit-then-ignore-for-400ms, nothing after) silently lost whatever event landed inside
        /// that window with no later event to 'wake it back up', which is exactly what happened
        /// when e.g. Claude's Edit tool wrote several files in a row: everything but the first write
        /// vanished until something unrelated (switching projects and back) forced a fresh reload."
        /// </para>
        /// <para>
        /// Rewriting this as a debounce reintroduces that bug. It is a fix, not an accident.
        /// </para>
        /// </remarks>
        private async Task RunAsync(CancellationToken cancellationToken)
        {
            // Far enough in the past that the first real event emits at once rather than waiting
            // out a throttle window nothing has used.
            var lastEmit = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 10);
            var pending = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                await DrainAsync(cancellationToken).ConfigureAwait(false);

                while (_events.Reader.TryRead(out var isNoise))
                {
                    // Noise never sets the flag; anything else does, including the error path,
                    // which posts `false` precisely so it cannot be filtered out.
                    pending |= !isNoise;
                }

                if (pending && Stopwatch.GetElapsedTime(lastEmit) >= Throttle)
                {
                    pending = false;
                    lastEmit = Stopwatch.GetTimestamp();

                    await PublishAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Waits for an event, or for the poll interval to pass — whichever comes first.</summary>
        private async Task DrainAsync(CancellationToken cancellationToken)
        {
            if (_events.Reader.TryPeek(out _))
            {
                return;
            }

            using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tick.CancelAfter(PollInterval);

            try
            {
                await _events.Reader.WaitToReadAsync(tick.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The tick expired with nothing waiting, which is 1.7.2's timeout arm: a
                // no-op that lets the pending flag be looked at again.
            }
        }

        private async Task PublishAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var payload = JsonSerializer.SerializeToDocument(
                    new RepoChangedEvent(_repoPath), WatcherJsonContext.Default.RepoChangedEvent);

                await _publish("repo:fs-changed", payload.RootElement, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                // The transport went away; the loop's own token will end it on the next pass.
            }
        }

        public async ValueTask DisposeAsync()
        {
            _watcher.EnableRaisingEvents = false;
            await _stopping.CancelAsync().ConfigureAwait(false);
            _events.Writer.TryComplete();

            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping is how this loop ends.
            }

            _watcher.Dispose();
            _stopping.Dispose();
        }
    }
}

/// <summary>The payload of <c>repo:fs-changed</c>.</summary>
/// <remarks>
/// <c>repo_path</c>, in snake_case: 1.7.2's struct carries no <c>rename_all</c>, and
/// <c>events.ts</c> reads that name. Terminal events are camelCase — the rule is to match what the
/// reference actually serialised, not to be consistent across features.
/// </remarks>
internal sealed record RepoChangedEvent([property: JsonPropertyName("repo_path")] string RepoPath);

[JsonSerializable(typeof(RepoChangedEvent))]
internal sealed partial class WatcherJsonContext : JsonSerializerContext;
