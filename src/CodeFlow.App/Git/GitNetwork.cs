using System.Diagnostics;
using System.Text.Json;
using CodeFlow.Ipc;

namespace CodeFlow.Git;

/// <summary>One line a streamed git operation printed.</summary>
public sealed record GitProgressEvent(string Op, string Line);

/// <summary>A streamed git operation finished.</summary>
public sealed record GitDoneEvent(string Op, bool Success, string Message);

/// <summary>
/// Clone, fetch, pull and push (GIT-034).
/// </summary>
/// <remarks>
/// <para>
/// <b>These four shell out to the system <c>git</c> binary, and that is the point</b>
/// (<c>DIVERGENCE-GIT-c</c>). Every other operation in this domain goes through libgit2, but the
/// ones that talk to a server run the same <c>git</c> the user's terminal runs, so SSH keys,
/// credential managers and <c>includeIf</c> config keep working untouched. No libgit2 credential
/// callback exists anywhere in 1.7.2, which is exactly why libgit2's much thinner
/// authentication support never bites.
/// </para>
/// <para>
/// The child process only ever runs the literal argument list built here. There is no shell
/// string and no user-controlled command surface.
/// </para>
/// </remarks>
public sealed class GitNetwork(PublishEvent publish)
{
    /// <summary>Clones into <paramref name="dest"/>, with no working directory of its own.</summary>
    public Task CloneAsync(string url, string dest, CancellationToken cancellationToken) =>
        RunStreamedAsync("clone", cwd: null, ["clone", url, dest], cancellationToken);

    /// <summary>Fetches a remote, defaulting to <c>origin</c>.</summary>
    public Task FetchAsync(string repoPath, string? remote, CancellationToken cancellationToken) =>
        RunStreamedAsync("fetch", repoPath, ["fetch", remote ?? "origin"], cancellationToken);

    /// <summary>
    /// Fetches one explicit refspec rather than the remote's default branches.
    /// </summary>
    /// <remarks>
    /// Not a command. The PR review pipeline calls it to pull a pull request's exact head ref,
    /// which is what makes reviewing a fork's PR work at all. It reports itself as <c>fetch</c>.
    /// </remarks>
    public Task FetchRefspecAsync(string repoPath, string remote, string refspec, CancellationToken cancellationToken) =>
        FetchRefspecsAsync(repoPath, remote, [refspec], cancellationToken);

    /// <summary>
    /// Fetches several explicit refspecs in one exchange with the remote.
    /// </summary>
    /// <remarks>
    /// One <c>git fetch</c> rather than one per ref: the negotiation, the connection and the
    /// authentication are paid once. The review pipeline is the caller that needs it — it wants a
    /// pull request's head and the branch it targets, and nothing else the remote has.
    /// </remarks>
    public Task FetchRefspecsAsync(
        string repoPath, string remote, IReadOnlyList<string> refspecs, CancellationToken cancellationToken) =>
        RunStreamedAsync("fetch", repoPath, ["fetch", remote, .. refspecs], cancellationToken);

    /// <summary>
    /// Pulls with <c>--no-edit</c>, so the repository's own merge or rebase default applies but a
    /// non-fast-forward merge accepts git's own generated commit message instead of trying to open
    /// an interactive editor (GIT-037). There is no TTY to open one against — the child's stdin is
    /// never redirected (<see cref="RunStreamedAsync"/>) — so without this flag, a divergent pull
    /// could fail partway through with the merge already applied to the working tree but never
    /// committed. A fast-forward pull is unaffected either way: no merge commit is ever created.
    /// </summary>
    public Task PullAsync(string repoPath, CancellationToken cancellationToken) =>
        RunStreamedAsync("pull", repoPath, ["pull", "--no-edit"], cancellationToken);

    /// <summary>
    /// Pushes, optionally setting the upstream.
    /// </summary>
    /// <remarks>
    /// <b><c>origin</c> is hardcoded</b> when setting the upstream — not read from the repository's
    /// remotes — so on a repository whose only remote is named something else, <c>push -u</c>
    /// fails. Ported as-is; it is 1.7.2's behaviour, not an oversight to correct here.
    /// </remarks>
    public Task PushAsync(string repoPath, bool setUpstream, CancellationToken cancellationToken)
    {
        if (!setUpstream)
        {
            return RunStreamedAsync("push", repoPath, ["push"], cancellationToken);
        }

        using var repo = RepoStatus.Open(repoPath);

        if (repo.Info.IsHeadDetached || repo.Info.IsHeadUnborn)
        {
            throw new InvalidOperationException("cannot push -u from a detached HEAD");
        }

        return RunStreamedAsync("push", repoPath, ["push", "-u", "origin", repo.Head.FriendlyName], cancellationToken);
    }

    /// <summary>
    /// Runs <c>git</c>, publishing every line of both streams as it arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// stdout and stderr are treated identically rather than one being an error channel: git
    /// writes most of its progress to stderr, so splitting them would leave the progress log
    /// empty and put "Receiving objects" in an error banner.
    /// </para>
    /// <para>
    /// The token is accepted but never aborts the process, deliberately. CodeFlow 1.7.2 has no
    /// kill, timeout or abort path for any of these four (<c>AMBIGUOUS-GIT-b</c>), and adding one
    /// would be new behaviour rather than a port — it would also need a <c>git:done</c> shape for
    /// "cancelled" that no frontend listener expects.
    /// </para>
    /// </remarks>
    private async Task RunStreamedAsync(string op, string? cwd, string[] args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (cwd is not null)
        {
            startInfo.WorkingDirectory = cwd;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start git");

        var stdout = PumpAsync(process.StandardOutput, op, cancellationToken);
        var stderr = PumpAsync(process.StandardError, op, cancellationToken);

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        var stdoutLines = await stdout.ConfigureAwait(false);
        var stderrLines = await stderr.ConfigureAwait(false);

        var success = process.ExitCode == 0;

        // git writes most error detail to stderr, but a few rare misconfigurations only explain
        // themselves on stdout — falling back keeps the UI from showing a bare failure with no
        // reason. The last resort is the exit-status label, which is all that is left to report.
        var detail = stderrLines.Count > 0
            ? string.Join("\n", stderrLines)
            : stdoutLines.Count > 0
                ? string.Join("\n", stdoutLines)
                : $"git {op} exited with exit status: {process.ExitCode}";

        await PublishAsync(
            "git:done", new GitDoneEvent(op, success, success ? "ok" : detail), cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            // Related to the event's message but not identical: the rejected promise carries the
            // operation name as a prefix, and the event does not.
            throw new InvalidOperationException($"git {op} failed: {detail}");
        }
    }

    /// <summary>Publishes each line as it is read, and keeps them for the completion message.</summary>
    private async Task<List<string>> PumpAsync(StreamReader reader, string op, CancellationToken cancellationToken)
    {
        var collected = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            collected.Add(line);
            await PublishAsync("git:progress", new GitProgressEvent(op, line), cancellationToken).ConfigureAwait(false);
        }

        return collected;
    }

    private async ValueTask PublishAsync<T>(string eventName, T payload, CancellationToken cancellationToken)
    {
        using var document = JsonSerializer.SerializeToDocument(payload, typeof(T), GitJsonContext.Default);
        await publish(eventName, document.RootElement, cancellationToken).ConfigureAwait(false);
    }
}
