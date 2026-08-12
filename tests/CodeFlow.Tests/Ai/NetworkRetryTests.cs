using System.Diagnostics;
using CodeFlow.Ai;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// A run that died before its CLI reached the network gets one more go.
/// </summary>
/// <remarks>
/// <para>
/// The failure this answers, observed on 2026-08-12: name resolution stopped working for about two
/// minutes and took an entire ticket review with it — the CLI never got past its own eligibility
/// check, so nothing was reviewed, nothing was spent, and the user was left with a red row that
/// looked like a defect in the app. Nothing anywhere retried.
/// </para>
/// <para>
/// Against a real subprocess, like <see cref="StdinDeliveryTests"/> and for the same reason: what is
/// under test is the runner deciding to spawn a second time, which only means anything if there was
/// a first process to fail. The script fails once and succeeds after, using a marker file as its
/// memory, so "did it run twice" is answered by the result rather than by counting.
/// </para>
/// </remarks>
public sealed class NetworkRetryTests : IDisposable
{
    /// <summary>Worded as Go's resolver words it, because that is what the CLIs are written in.</summary>
    private const string ResolverFailure = "Error: dial tcp: lookup example.invalid: no such host";

    private readonly string _marker = Path.Combine(Path.GetTempPath(), $"codeflow-retry-{Guid.NewGuid():N}");

    [Fact]
    public async Task A_review_whose_CLI_never_reached_the_network_is_simply_run_again()
    {
        var result = await RunAsync(autoApproveEdits: false);

        Assert.Equal("a review of something", result.Text);
        Assert.True(File.Exists(_marker), "the first attempt must actually have happened");
    }

    [Fact]
    public async Task A_run_that_may_have_written_files_is_never_repeated()
    {
        // The same transient failure, from a flow that can edit the repository. Retrying it would
        // apply whatever the first attempt had already done a second time, and nothing here can know
        // how far that attempt got — so the answer is the failure, not a repeat.
        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => RunAsync(autoApproveEdits: true));

        Assert.Contains("no such host", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_that_is_not_the_network_still_fails_the_first_time()
    {
        var registry = new AiRunRegistry(Recorder.Create().Publish);
        var engine = new FlakyShellEngine(_marker, "printf 'the model refused\\n' >&2; exit 1");
        using var http = new HttpClient();

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(() => AiEngineRunner.RunAsync(
            new AiConfig(engine, engine.Id, string.Empty, engine.DefaultBinary, null),
            new AiInvocation("review this", string.Empty),
            run: null,
            registry,
            http,
            TestContext.Current.CancellationToken));

        Assert.Contains("the model refused", failure.Message, StringComparison.Ordinal);

        // Proven by absence: a second attempt would have found the marker and succeeded.
        Assert.True(File.Exists(_marker));
    }

    private async Task<AiRun> RunAsync(bool autoApproveEdits)
    {
        var registry = new AiRunRegistry(Recorder.Create().Publish);
        var engine = new FlakyShellEngine(_marker, $"printf '{ResolverFailure}\\n' >&2; exit 1");
        using var http = new HttpClient();

        return await AiEngineRunner.RunAsync(
            new AiConfig(engine, engine.Id, string.Empty, engine.DefaultBinary, null),
            new AiInvocation("review this", string.Empty, AutoApproveEdits: autoApproveEdits),
            run: null,
            registry,
            http,
            TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        File.Delete(_marker);
    }

    /// <summary>
    /// An engine whose CLI fails the first time it is run and answers the second.
    /// </summary>
    /// <remarks>
    /// The marker file is the memory: the script creates it on the way out of its failing branch, so
    /// a second spawn takes the other one. That makes the assertion a plain equality on the reply
    /// rather than a count of invocations, and it fails loudly if the retry ever stops happening.
    /// </remarks>
    private sealed class FlakyShellEngine(string marker, string failure) : IAiEngine
    {
        public string Id => "flaky";

        public string Label => "Flaky";

        public string DefaultBinary => PosixShell();

        public string StdinPayload(AiInvocation invocation) => string.Empty;

        public ProcessStartInfo BuildCommand(string binary, AiInvocation invocation)
        {
            var info = new ProcessStartInfo { FileName = PosixShell() };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(
                $"if [ -e '{marker}' ]; then printf 'a review of something\\n'; else : > '{marker}'; {failure}; fi");
            return info;
        }

        public AiRun Interpret(bool success, string statusLabel, string stdout, string stderr) =>
            success
                ? new AiRun(stdout.Trim(), null, null)
                : throw new AiRunFailedException($"flaky exited with an error ({statusLabel}): {stderr.Trim()}");

        private static string PosixShell()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "the script under test is POSIX shell.");
            return "/bin/sh";
        }
    }
}
