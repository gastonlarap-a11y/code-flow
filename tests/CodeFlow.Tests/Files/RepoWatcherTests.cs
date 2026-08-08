using System.Collections.Concurrent;
using System.Text.Json;
using CodeFlow.Files;
using Xunit;

namespace CodeFlow.Tests.Files;

/// <summary>
/// The working-tree watcher, against the acceptance checklist in
/// <c>docs/business-rules/11-files-search-terminal.md</c> (<c>FILE-012</c>, <c>FILE-013</c>).
/// </summary>
/// <remarks>
/// <para>
/// No vectors exist: the implementation has no extracted cases, so the checklist's five points
/// are the whole executable specification, and each is one test below.
/// </para>
/// <para>
/// A real directory and the real OS watcher, because what is being asserted is the timing of a
/// throttle around events only the platform can produce. Serialised with the other tests that watch
/// the clock: a machine under load would make the waits flaky rather than the code wrong.
/// </para>
/// </remarks>
[Collection(SerialTemporaryFiles.Name)]
public sealed class RepoWatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Comfortably past one throttle window plus one poll tick (400 ms + 200 ms).</summary>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(700);

    [Fact]
    public async Task Emissions_stop_once_a_write_has_been_reported()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();

        fixture.Write("a.ts", "one");

        await fixture.WaitForAsync(1);

        // Let whatever the OS still had in flight for that one write drain. `FileSystemWatcher` is
        // free to report a single `File.WriteAllText` as a creation *and* a change, and if the
        // second one lands after the first emission the throttle reports it a window later. That is
        // the design working: `FILE-012` promises a flush within ~600 ms of the first event in a
        // burst, and never promises a fixed number of emissions per write.
        await Task.Delay(SettleWindow, Ct);
        var settled = fixture.Count;

        // What it must not do is keep going. A `pending` flag left set by an event that had already
        // been reported re-arms itself on every poll, so the count would climb for as long as the
        // watch lives. That is the fault this guards — and asserting a fixed total never told it
        // apart from the OS having simply spoken twice, which is what made this test flaky.
        await Task.Delay(SettleWindow, Ct);

        Assert.Equal(settled, fixture.Count);
        Assert.Equal(fixture.Path, Assert.Single(fixture.Paths.Distinct()));
    }

    /// <summary>
    /// <c>DIVERGENCE-FILE-b</c>: the one behaviour that separates this from a plain throttle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A change landing inside the window, with nothing after it to wake the watcher back up, must
    /// still be reported once the window closes. A plain leading-edge throttle — emit, then ignore
    /// for 400 ms, and nothing afterwards — loses it forever, which is the concrete bug the
    /// reference's comment describes: several files written in a row, everything but the first
    /// vanishing until something unrelated forced a reload.
    /// </para>
    /// <para>
    /// The second write goes out as soon as the first emission is seen, so it lands well inside the
    /// window rather than starting a fresh one — which is what makes this discriminate rather than
    /// merely pass.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_change_inside_the_window_is_flushed_afterwards_rather_than_dropped()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();

        fixture.Write("a.ts", "one");
        await fixture.WaitForAsync(1);

        // Exactly one write, immediately, and then silence. Nothing will arrive later to carry it.
        fixture.Write("b.ts", "two");

        await fixture.WaitForAsync(2);
    }

    [Fact]
    public async Task A_burst_is_never_reported_as_nothing()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();

        for (var i = 0; i < 20; i++)
        {
            fixture.Write($"file{i}.ts", $"contents {i}");
        }

        var afterBurst = DateTime.UtcNow;
        await fixture.WaitForAsync(2);

        Assert.True(
            fixture.LastAt > afterBurst,
            "the pending change was flushed after the burst, not only before it");
    }

    [Fact]
    public async Task Git_bookkeeping_files_produce_nothing()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();

        fixture.Write("index.lock", "");
        fixture.Write("FETCH_HEAD", "abc123");
        fixture.Write("COMMIT_EDITMSG", "wip");

        // Long enough that a real change would have been reported twice over.
        await Task.Delay(900, Ct);

        Assert.Equal(0, fixture.Count);
    }

    [Fact]
    public async Task Starting_twice_on_the_same_repo_does_not_leave_two_watchers()
    {
        await using var fixture = new Fixture();

        await fixture.StartAsync();
        await fixture.StartAsync();

        Assert.Equal(1, fixture.Watcher.Count);

        // And the survivor is live: a replaced watch that killed the wrong one would report nothing.
        fixture.Write("a.ts", "one");
        await fixture.WaitForAsync(1);
    }

    [Fact]
    public async Task Stopping_a_repo_nobody_watches_is_not_an_error()
    {
        await using var fixture = new Fixture();

        await fixture.Watcher.StopAsync(fixture.Path);

        Assert.Equal(0, fixture.Watcher.Count);
    }

    [Fact]
    public async Task Stopping_a_watch_stops_its_events()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.Write("a.ts", "one");
        await fixture.WaitForAsync(1);

        await fixture.Watcher.StopAsync(fixture.Path);
        var before = fixture.Count;

        fixture.Write("b.ts", "two");
        await Task.Delay(900, Ct);

        Assert.Equal(before, fixture.Count);
    }

    /// <summary>A watcher over a temporary directory, with every event it published.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _directory = new();
        private readonly ConcurrentQueue<(string Path, DateTime At)> _events = new();

        public Fixture() => Watcher = new RepoWatcher(Record);

        public RepoWatcher Watcher { get; }

        public string Path => _directory.Path;

        public int Count => _events.Count;

        public IEnumerable<string> Paths => _events.Select(e => e.Path);

        public DateTime LastAt => _events.Max(e => e.At);

        public ValueTask StartAsync() => Watcher.StartAsync(Path, Ct);

        public void Write(string name, string content) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), content);

        /// <summary>Waits until at least <paramref name="count"/> events have been published.</summary>
        public async Task WaitForAsync(int count)
        {
            // Generous, because it waits on the OS: a slow machine should make this slower, not red.
            var deadline = DateTime.UtcNow.AddSeconds(15);

            while (DateTime.UtcNow < deadline)
            {
                if (Count >= count)
                {
                    return;
                }

                await Task.Delay(25, Ct);
            }

            Assert.Fail($"timed out waiting for {count} repo:fs-changed; saw {Count}");
        }

        private ValueTask Record(string name, JsonElement payload, CancellationToken cancellationToken)
        {
            Assert.Equal("repo:fs-changed", name);

            // repo_path, in snake_case: events.ts reads that name, and the renderer filters on it.
            _events.Enqueue((payload.GetProperty("repo_path").GetString()!, DateTime.UtcNow));

            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await Watcher.DisposeAsync();
            _directory.Dispose();
        }
    }
}
