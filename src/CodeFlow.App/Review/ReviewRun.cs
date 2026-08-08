using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CodeFlow.Ai;
using CodeFlow.Activity;
using CodeFlow.Git;
using CodeFlow.Platform;
using CodeFlow.Providers;
using CodeFlow.Storage;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Review;

/// <summary>
/// The two review entry points.
/// </summary>
/// <remarks>
/// <para>
/// One reviews a pull request against the project's local clone; the other reviews one reached by a
/// pasted link alone, with no clone and no project row. They share the AI operation and the config
/// they run under, and differ in everything else — where the diff comes from, what the working
/// directory is, and whether anything is remembered afterwards.
/// </para>
/// <para>
/// Both paths sync the workspace's skills into their working directory first, best-effort, before
/// invoking the engine — the clone for one, the ad-hoc directory for the other. See
/// <see cref="Workspaces.SkillSync"/>.
/// </para>
/// </remarks>
internal static class ReviewRun
{
    /// <summary>
    /// Tells the model, up front, that it is reviewing without the surrounding codebase.
    /// </summary>
    /// <remarks>
    /// <c>VERBATIM</c>, Spanish, and load-bearing: without it the model tries to open files that are
    /// not there and asserts findings it cannot demonstrate. It reaches the model as a review
    /// context, which is why it is one long line rather than a paragraph.
    /// </remarks>
    private const string NoCloneContext =
        "Esta revisión corre SIN un clon local del repositorio. Por stdin recibes el diff completo del "
        + "pull request, y el directorio de trabajo solo contiene `PULL_REQUEST.md` y `changes.diff`. No "
        + "intentes explorar el árbol del repositorio ni abrir archivos que no estén ahí. Basa la revisión "
        + "en el diff: cuando un hallazgo dependa de código que no ves (una función llamada pero no "
        + "incluida, un contrato definido en otro archivo), decláralo explícitamente y baja la confianza en "
        + "consecuencia, o clasifícalo como Security Hotspot en lugar de afirmar un bug que no puedes "
        + "demostrar.";

    /// <summary>Reviews a linked project's pull request against its local clone.</summary>
    /// <remarks>
    /// The steps that look skippable and are not: the fetch is best-effort and its result discarded,
    /// so an offline machine still reviews against whatever refs it has; the pull request is found by
    /// listing and filtering rather than by the host's single-pull-request endpoint, which is what
    /// makes a GitHub pull request older than the newest hundred unreachable here; and a re-review on
    /// the same head commit returns without calling the model at all.
    /// </remarks>
    public static async Task<string> ForProjectAsync(
        Database database,
        HttpClient http,
        GitNetwork git,
        AiRunner runner,
        string projectId,
        long prId,
        string jobId,
        string level,
        AgentOverride agent,
        CancellationToken cancellationToken)
    {
        // Started here rather than around the engine call: what the user waits through is the whole
        // operation — the fetch, the diff, the model, the reconciliation — and a figure that
        // measured only part of it would be answering a question nobody asked.
        var started = Stopwatch.StartNew();

        var (project, link, config, template, mcps) = await database.ReadAsync(
            c => Setup(c, projectId, level, agent), cancellationToken).ConfigureAwait(false);

        var host = PullRequestHosts.For(http, link);

        // A full list and a filter, never the host's get-by-number endpoint. Faithful, and the reason
        // a GitHub pull request past the hundred most recent cannot be reviewed from here.
        var pullRequests = await host.ListPullRequestsAsync(cancellationToken).ConfigureAwait(false);
        var pr = pullRequests.FirstOrDefault(p => p.Id == prId)
            ?? throw new ReviewException("Pull request not found");

        // Only the refs this review will read.
        //
        // This was a bare `git fetch origin` — every branch and tag the remote has, to diff two of
        // them. On a repository with any history that is the slowest thing here by a wide margin,
        // and it is not bounded by anything: `GitNetwork` runs git to completion and its
        // cancellation token deliberately never aborts the process (`AMBIGUOUS-GIT-b`), so the
        // ten-minute deadline on the model does not cover it either. Narrowing what is asked for
        // reaches the same place without reopening that decision.
        //
        // The head is fetched separately by `HeadRefAsync`, which for GitHub wants `refs/pull/N/head`
        // — a fork's branch does not exist on origin at all, so it cannot be named here.
        var refspecs = new List<string>
        {
            Refspec(pr.TargetBranch),
        };

        if (link is not LinkedRepo.GitHub)
        {
            refspecs.Add(Refspec(pr.SourceBranch));
        }

        // Best-effort: offline, or an auth hiccup, must not block a review that can still run against
        // the refs already on disk.
        try
        {
            await git.FetchRefspecsAsync(project.LocalPath, "origin", refspecs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Discarded exactly as 1.7.2 discards it.
        }

        var headRef = await HeadRefAsync(git, link, project.LocalPath, pr, prId, cancellationToken)
            .ConfigureAwait(false);

        // Off the pump thread: LibGit2Sharp is synchronous and a branch diff is not bounded work.
        var headSha = await Task.Run(() => ResolveSha(project.LocalPath, headRef), cancellationToken)
            .ConfigureAwait(false);

        var previousHead = await database.ReadAsync(
            c => Safely(() => ReviewRunStore.LatestHead(c, projectId, prId)), cancellationToken)
            .ConfigureAwait(false);

        if (headSha.Length > 0 && previousHead == headSha)
        {
            // No model call, no job-history row, no review-run row: a pure read and an early return.
            return string.Create(CultureInfo.InvariantCulture,
                $"🔁 Sin cambios desde la última revisión (mismo commit `{headSha[..Math.Min(8, headSha.Length)]}`). No se volvió a analizar.");
        }

        // Only computed when there is a previous head to diff against. Leaving it null degrades
        // reconciliation to full-review semantics, where an unmatched finding is treated as resolved.
        var changedFiles = previousHead is { Length: > 0 }
            ? await Task.Run(
                () => Safely(() => Diff.ChangedFilesBetween(project.LocalPath, previousHead, headRef)),
                cancellationToken).ConfigureAwait(false)
            : null;

        await SyncSkillsAsync(database, project.WorkspaceId, project.LocalPath, cancellationToken)
            .ConfigureAwait(false);

        // One diff, shaped twice: what changed, and the code it changed inside of. The second is
        // what the model used to go and read for itself, one file at a time.
        var files = await Task.Run(
            () => Diff.BranchDiff(project.LocalPath, pr.TargetBranch, headRef), cancellationToken)
            .ConfigureAwait(false);

        var (reviewable, carried) = Narrow(files, changedFiles);
        var (diffText, coverage) = PromptDiff.Shape(reviewable, PromptDiff.DefaultBudgetChars, carried);
        var codeContext = ChangeContext.Render(reviewable);

        var contexts = Contexts(await database.ReadAsync(
            c => ReviewContextStore.List(c, project.WorkspaceId), cancellationToken).ConfigureAwait(false));

        if (!string.IsNullOrWhiteSpace(agent.Prompt))
        {
            contexts.Insert(0, ("Agent", agent.Prompt));
        }

        var label = string.Create(CultureInfo.InvariantCulture, $"#{pr.Id} {pr.Title}");
        var meta = JsonSerializer.Serialize(
            new ReviewJobMeta(pr.Id, pr.Title, level), ReviewJsonContext.Default.ReviewJobMeta);

        try
        {
            var review = await AiOperations.ReviewPullRequestAsync(
                runner, config, pr.Title, pr.Description, contexts, diffText, codeContext, project.LocalPath,
                template, level, explorable: true,
                McpConfig.Write(mcps, AppPaths.WorkspaceMcpConfigFile(project.WorkspaceId)),
                new AiRunContext(jobId), cancellationToken).ConfigureAwait(false);

            var text = await PersistAsync(
                database, jobId, project, pr, level, config, diffText, headSha, changedFiles, review,
                coverage, started.Elapsed, cancellationToken).ConfigureAwait(false);

            // Best-effort, like the memory write: a history failure must not turn a good review into a
            // reported failure.
            await RecordAsync(
                database, jobId, projectId, label, "done", text, null, meta, cancellationToken)
                .ConfigureAwait(false);

            return text;
        }
        catch (AiRunFailedException failure)
        {
            // A run the user stopped leaves nothing behind — no history row and no saved memory.
            if (!failure.Message.StartsWith(AiRunRegistry.CancelledMarker, StringComparison.Ordinal))
            {
                await RecordAsync(
                    database, jobId, projectId, label, "error", null, failure.Message, meta, cancellationToken)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Reviews a pull request reached by its link alone.</summary>
    /// <remarks>
    /// This trades depth for reach on purpose: the model sees the diff and the description but not the
    /// surrounding codebase, so it cannot confirm a caller or check whether a test exists — and the
    /// prompt says so, in <see cref="NoCloneContext"/>. Nothing is persisted either, success or
    /// failure: a run with no project has no project to file itself under, and a run with no stored
    /// row has nothing for a later re-review to reconcile against. Every call re-runs the full
    /// analysis, with no delta banner and no thread reuse.
    /// </remarks>
    public static async Task<string> ForLinkAsync(
        Database database,
        HttpClient http,
        AiRunner runner,
        string url,
        string jobId,
        string level,
        string workspaceId,
        AgentOverride agent,
        string reviewsRoot,
        CancellationToken cancellationToken)
    {
        // Parsed and credentialled before anything else, so an unreadable link fails without a
        // network call.
        var target = await database.ReadAsync(c => PrLink.Parse(url, KnownHosts.ForGitHub(c)), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ProviderException("That isn't a pull-request link CodeFlow can read");

        var started = Stopwatch.StartNew();

        var (config, template, mcps, contexts) = await database.ReadAsync(
            c => (
                // No checkout means no tools, at every level: the working directory holds a
                // description and a diff, so a file-reading tool here can only fail or mislead.
                AiRouting.Bound(c, Routed(c, agent), Toolset(level, explorable: false)),
                Settings.GetWorkspacePrompt(c, workspaceId, "review_standard"),
                WorkspaceMcpStore.List(c, workspaceId),
                ReviewContextStore.List(c, workspaceId)),
            cancellationToken).ConfigureAwait(false);

        var (host, number) = PullRequestHosts.For(http, target);
        var (pr, rawDiff) = await host.FetchPullRequestAndDiffAsync(number, cancellationToken).ConfigureAwait(false);

        // Shaped like every other prompt diff (`GIT-031`). This is the one route whose diff never
        // came through libgit2 — the host hands it back as text — and so the one the budget missed
        // when it moved out of `AiOperations`: the provider's diff went into the prompt whole.
        var diffText = PromptDiff.RenderText(rawDiff, PromptDiff.DefaultBudgetChars);

        // No `CODE AROUND THE CHANGES` here, and there is no way to build one: the host hands back a
        // patch, not the files it came from, so the only code this review will ever see is whatever
        // context the provider chose to include.

        // The workspace file keeps the diff as the provider wrote it. It is there for the user to
        // open, not for the model to read, and truncating what someone might scroll through would
        // be losing information to solve a problem it does not have.
        var workingDirectory = LinkWorkspace(target, pr, rawDiff, reviewsRoot);

        // The ad-hoc directory, not a checkout: a link-only review still gets the workspace's
        // skills, because the engine runs with that directory as its working directory.
        await SyncSkillsAsync(database, workspaceId, workingDirectory, cancellationToken).ConfigureAwait(false);

        var enabled = Contexts(contexts);
        enabled.Insert(0, ("Modo de revisión", NoCloneContext));
        if (!string.IsNullOrWhiteSpace(agent.Prompt))
        {
            // Ahead of the no-clone warning: the agent's own role frames the whole review.
            enabled.Insert(0, ("Agent", agent.Prompt));
        }

        var review = await AiOperations.ReviewPullRequestAsync(
            runner, config, pr.Title, pr.Description, enabled, diffText, codeContext: "", workingDirectory,
            template, level, explorable: false,
            McpConfig.Write(mcps, AppPaths.WorkspaceMcpConfigFile(workspaceId)),
            new AiRunContext(jobId), cancellationToken).ConfigureAwait(false);

        // Nothing is stored for a link review, so this line is the only record it leaves at all.
        return AiText.StampFooter(
            review.Text, "pr-review", config.Engine.Label, review.Model ?? config.Model, DateTimeOffset.Now,
            review.Usage, Details(level, started.Elapsed, coverage: null, findings: null, delta: null));
    }

    /// <summary>
    /// Saves one completed review as durable memory and, on a re-review, folds the delta into the
    /// text the user gets back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Best-effort end to end. Losing memory must never fail the review someone is waiting on, so a
    /// failed write is swallowed and the review is returned regardless.
    /// </para>
    /// <para>
    /// <b>The order of the three text mutations is load-bearing.</b> The history is appended first,
    /// the banner prepended second and the footer stamped last, giving
    /// <c>{banner}{review}{history}{footer}</c>. The footer has to come last because the renderer
    /// matches it anchored to the end of the text — stamped inside the operation, as it was, the
    /// history section landed after it and it stopped being findable. The same single value is both
    /// what is returned and what is stored, so what the user reads is exactly what is kept.
    /// </para>
    /// </remarks>
    private static async Task<string> PersistAsync(
        Database database,
        string jobId,
        Project project,
        PullRequestSummary pr,
        string level,
        AiConfig config,
        string diffText,
        string headSha,
        IReadOnlyList<string>? changedFiles,
        AiRun review,
        DiffCoverage coverage,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;

        string Stamp(string body, IReadOnlyList<MemoryFinding>? findings, ReviewDelta? delta) =>
            AiText.StampFooter(
                body, "pr-review", config.Engine.Label, review.Model ?? config.Model, now, review.Usage,
                Details(level, elapsed, coverage, findings, delta));

        try
        {
            var prior = await database.ReadAsync(
                c => (int)ReviewRunStore.Count(c, project.Id, pr.Id), cancellationToken).ConfigureAwait(false);

            var parsed = ReviewMemory.ParseFindings(review.Text);
            List<MemoryFinding> findings;
            ReviewDelta? delta = null;

            if (prior > 0)
            {
                var stored = await database.ReadAsync(
                    c => ReviewRunStore.LatestFindings(c, project.Id, pr.Id), cancellationToken)
                    .ConfigureAwait(false);

                // A missing row or malformed JSON reconciles against nothing rather than failing.
                var previous = Safely(() => stored is null
                    ? null
                    : JsonSerializer.Deserialize(stored, ReviewJsonContext.Default.ListMemoryFinding)) ?? [];

                (findings, var computed) = ReviewMemory.Reconcile(previous, parsed, prior, changedFiles, level);
                delta = computed;
            }
            else
            {
                findings = [.. parsed.Select(f => f with { IntroducidoEnIter = 1, Nivel = level })];
            }

            var iter = prior + 1;

            // DIVERGENCE-REVIEW-a: the model numbers its own findings from F-001 every run, while
            // Reconcile assigns the stable ids the posting flow reuses threads by. Aligning them here
            // is what stops the number a human reads from naming a different finding than the one a
            // click acts on. A first review has nothing to reconcile against and is left alone.
            var text = delta is null ? review.Text : ReviewMemory.RenumberHeaders(review.Text, parsed, findings);

            // Before the resolved history, because these are the ones still asking for something:
            // everything still open that this run never restated, because its file had not moved.
            if (ReviewMemory.PersistingSection(findings, parsed) is { } open)
            {
                text += open;
            }

            if (ReviewMemory.ResolvedHistorySection(findings) is { } history)
            {
                text += history;
            }

            if (delta is not null)
            {
                text = ReviewMemory.DeltaBanner(delta) + text;
            }

            text = Stamp(text, findings, delta);

            var meta = new ReviewMeta(
                pr.Id, pr.Title, pr.Description, pr.Author, pr.SourceBranch, pr.TargetBranch, pr.Url, pr.Provider,
                level, config.Engine.Label, config.Model, project.Id, project.Name, project.WorkspaceId,
                // Local machine time, not UTC — 1.7.2's chrono::Local.
                now.ToString("o", CultureInfo.InvariantCulture), iter, headSha,
                // Kept as numbers as well as in the footer's sentence: comparing two runs should not
                // mean parsing prose out of a stored markdown blob.
                review.Usage, (long)elapsed.TotalMilliseconds, coverage);

            await database.WriteAsync(
                c => ReviewRunStore.Add(
                    c, jobId, project.Id, project.WorkspaceId, pr.Id, iter, level,
                    JsonSerializer.Serialize(meta, ReviewJsonContext.Default.ReviewMeta),
                    text,
                    diffText,
                    JsonSerializer.Serialize(findings, ReviewJsonContext.Default.ListMemoryFinding)),
                cancellationToken).ConfigureAwait(false);

            return text;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The review is already in hand; losing its memory is not worth losing the review over.
            // It still gets a footer — what the run cost is known regardless of whether it was filed.
            return Stamp(review.Text, findings: null, delta: null);
        }
    }

    /// <summary>
    /// What a review of this depth may reach for, now that the code arrives with the question.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty for every level but <c>ultra</c>, and empty for <c>ultra</c> too when there is no
    /// checkout under it. That is not a saving taken out of the review's depth: the
    /// <c>CODE AROUND THE CHANGES</c> section now carries the whole declaration around every change,
    /// which is what the model was opening files to reconstruct — nineteen <c>Read</c>s and seven
    /// <c>Grep</c>s in one measured run, each one another round trip over a context that had grown
    /// since the last.
    /// </para>
    /// <para>
    /// <c>ultra</c> keeps them because <c>ultra</c> asks for something the extract genuinely cannot
    /// answer: following a callee into a file the change never touched.
    /// </para>
    /// </remarks>
    private static string[] Toolset(string level, bool explorable) =>
        explorable && level == "ultra" ? AiRouting.RecommendedTools : [];

    /// <summary>Routing for a review, whether or not a workspace agent is driving it.</summary>
    private static AiConfig Routed(SqliteConnection connection, AgentOverride agent) =>
        agent.RoutesItself
            ? AiRouting.ResolveFor(connection, agent.Provider!, agent.Model!, "review")
            : AiRouting.Resolve(connection, "review");

    /// <summary>
    /// Drops from a re-review the files that have not moved since the previous one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Their findings travel forward on their own: <c>ReviewMemory.Reconcile</c> keeps a finding
    /// open when its file did not change, precisely so a re-review is not obliged to rediscover it.
    /// Re-reading them, then, buys nothing and costs the whole file's diff and extract — on the
    /// fifth review of one pull request, most of the prompt.
    /// </para>
    /// <para>
    /// <b>The trade is real and is declared</b>: a new problem in an untouched file, caused by a
    /// change in another, will not be found this round. The notice names every file skipped, and a
    /// review of the pull request from scratch sees all of them again.
    /// </para>
    /// </remarks>
    private static (IReadOnlyList<FileDiffInfo> Reviewable, IReadOnlyList<string> Carried) Narrow(
        IReadOnlyList<FileDiffInfo> files, IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is not { Count: > 0 })
        {
            return (files, []);
        }

        var moved = changedFiles.ToHashSet(StringComparer.Ordinal);
        var reviewable = files.Where(file => moved.Contains(PathOf(file))).ToList();

        // Nothing in common — a rename the two diffs describe differently, a base branch that moved
        // under both. Reviewing nothing is never the right reading of that.
        return reviewable.Count == 0
            ? (files, [])
            : (reviewable, [.. files.Where(file => !moved.Contains(PathOf(file))).Select(PathOf)]);
    }

    private static string PathOf(FileDiffInfo file) => file.NewPath ?? file.OldPath ?? "?";

    /// <summary>
    /// What the run cost and how much of the change it saw, for the line under the review.
    /// </summary>
    /// <remarks>
    /// Local-only by construction: it is appended to the footer, and every path that composes a
    /// comment for the pull-request host builds its text from the findings instead
    /// (<c>formatFindingAsComment</c>, <c>formatSummaryComment</c>). Segments are separated by
    /// <c>·</c> and never contain one, because the renderer splits on it.
    /// </remarks>
    private static List<string> Details(
        string level,
        TimeSpan elapsed,
        DiffCoverage? coverage,
        IReadOnlyList<MemoryFinding>? findings,
        ReviewDelta? delta)
    {
        var details = new List<string> { $"nivel {level}", Elapsed(elapsed) };

        if (coverage is not null)
        {
            details.Add(Seen(coverage));
        }

        if (findings is not null)
        {
            details.Add(Found(findings, delta));
        }

        return details;
    }

    /// <summary>How long it took, rounded to something a person compares at a glance.</summary>
    private static string Elapsed(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? FormattableString.Invariant($"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s")
        : FormattableString.Invariant($"{(int)elapsed.TotalSeconds} s");

    /// <summary>
    /// How much of the change reached the model, and what it did not.
    /// </summary>
    /// <remarks>
    /// Worded so the counts cannot be read as each other. <c>"diff: 34 de 52 archivos, 10
    /// recortados"</c> was read — reasonably — as "it only managed ten", when it means it saw
    /// thirty-four and had to cut ten of those short. Whole and trimmed are now named separately and
    /// add up to what was seen, and every other count says what it is rather than sitting in a list
    /// of numbers.
    /// </remarks>
    internal static string Seen(DiffCoverage coverage)
    {
        var whole = coverage.Shown - coverage.Truncated;
        var seen = coverage.Truncated > 0
            ? FormattableString.Invariant(
                $"diff: {coverage.Files} archivos · vio {coverage.Shown} ({whole} enteros, {coverage.Truncated} recortados)")
            : FormattableString.Invariant($"diff: {coverage.Files} archivos · vio {coverage.Shown} enteros");

        var missing = new List<string>();

        if (coverage.Excluded > 0)
        {
            missing.Add(FormattableString.Invariant($"{coverage.Excluded} excluidos por no aportar nada"));
        }

        if (coverage.Omitted > 0)
        {
            missing.Add(FormattableString.Invariant($"{coverage.Omitted} sin sitio en el prompt"));
        }

        if (coverage.Carried > 0)
        {
            missing.Add(FormattableString.Invariant($"{coverage.Carried} sin cambios desde la revisión anterior"));
        }

        return missing.Count == 0 ? seen : seen + " · " + string.Join(" · ", missing);
    }

    /// <summary>How many findings, and what moved since the last review.</summary>
    private static string Found(IReadOnlyList<MemoryFinding> findings, ReviewDelta? delta)
    {
        var active = findings.Count(f => f.Estado is not (MemoryFinding.Resolved or MemoryFinding.FalsePositive));
        var count = FormattableString.Invariant($"{active} hallazgos");

        return delta is null
            ? count
            : count + FormattableString.Invariant(
                $": {delta.Nuevos} nuevos, {delta.Persisten} persisten, {delta.Resueltos} resueltos");
    }

    /// <summary>The ref a project-backed review diffs against.</summary>
    /// <remarks>
    /// For GitHub, the pull request's canonical head is fetched into a local tracking ref, so the
    /// review reflects its exact head commit — including a fork's branch, which never exists as a
    /// normal <c>origin/*</c> ref. A failed fetch falls back to the source branch. Azure pull requests
    /// are always same-remote branches, so there is nothing to fetch and nothing to fall back from.
    /// </remarks>
    /// <summary>
    /// A branch's refspec, forced, into the remote-tracking ref the diff resolves through.
    /// </summary>
    /// <remarks>
    /// Forced (<c>+</c>) to match what a plain fetch does for a tracking ref, so a force-pushed
    /// branch updates here rather than being refused. `GIT-030` resolves `origin/{name}` before the
    /// bare local name, which is the ref this writes.
    /// </remarks>
    private static string Refspec(string branch) =>
        string.Create(CultureInfo.InvariantCulture, $"+refs/heads/{branch}:refs/remotes/origin/{branch}");

    private static async Task<string> HeadRefAsync(
        GitNetwork git,
        LinkedRepo link,
        string repoPath,
        PullRequestSummary pr,
        long prId,
        CancellationToken cancellationToken)
    {
        if (link is not LinkedRepo.GitHub)
        {
            return pr.SourceBranch;
        }

        var localRef = string.Create(CultureInfo.InvariantCulture, $"refs/remotes/origin/codeflow-pr-{prId}");
        try
        {
            await git.FetchRefspecAsync(
                repoPath, "origin",
                string.Create(CultureInfo.InvariantCulture, $"+refs/pull/{prId}/head:{localRef}"),
                cancellationToken).ConfigureAwait(false);

            return localRef;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return pr.SourceBranch;
        }
    }

    /// <summary>Lays out the working directory a repo-less review runs in.</summary>
    /// <remarks>
    /// The engine needs <em>some</em> working directory, and pointing it at an unrelated folder would
    /// be worse than useless — so it gets one holding this pull request's own description and diff.
    /// Reused and overwritten across re-runs of the same link rather than piling up directories;
    /// nothing here ever deletes one for a link that is never revisited.
    /// </remarks>
    /// <param name="reviewsRoot">
    /// Normally <see cref="AppPaths.PrLinkReviewsDirectory"/>. Taken as an argument rather than
    /// derived here so this stays testable without writing into the real application directory —
    /// the same seam <see cref="McpConfig.Write"/> uses.
    /// </param>
    private static string LinkWorkspace(
        PrLinkTarget target, PullRequestSummary pr, string diff, string reviewsRoot)
    {
        var slug = Slugify(target switch
        {
            PrLinkTarget.GitHub g => string.Create(CultureInfo.InvariantCulture,
                $"github-{g.Host}-{g.Owner}-{g.Repo}-{g.Number}"),
            PrLinkTarget.Azure a => string.Create(CultureInfo.InvariantCulture,
                $"azure-{a.Org}-{a.Project}-{a.Repo}-{a.Number}"),
            _ => throw new ProviderException("That isn't a pull-request link CodeFlow can read"),
        });

        var directory = Path.Combine(reviewsRoot, slug);
        Directory.CreateDirectory(directory);

        var description = string.IsNullOrWhiteSpace(pr.Description) ? "(sin descripción)" : pr.Description;
        File.WriteAllText(
            Path.Combine(directory, "PULL_REQUEST.md"),
            string.Create(CultureInfo.InvariantCulture,
                $"# #{pr.Id} {pr.Title}\n\n- Autor: {pr.Author}\n- Rama origen: `{pr.SourceBranch}`\n"
                + $"- Rama destino: `{pr.TargetBranch}`\n- URL: {pr.Url}\n\n## Descripción\n\n{description}\n"));

        File.WriteAllText(Path.Combine(directory, "changes.diff"), diff);
        return directory;
    }

    /// <summary>Keeps a directory name to what every filesystem accepts.</summary>
    private static string Slugify(string value) =>
        string.Create(value.Length, value, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsAsciiLetterOrDigit(source[i]) || source[i] is '-' or '_' ? source[i] : '-';
            }
        });

    /// <summary>Everything a project-backed review reads before it starts, under one gate.</summary>
    private static (Project Project, LinkedRepo Link, AiConfig Config, string Template, List<WorkspaceMcp> Mcps)
        Setup(SqliteConnection connection, string projectId, string level, AgentOverride agent)
    {
        var project = ProjectStore.Get(connection, projectId) ?? throw new ProviderException("Project not found");

        return (
            project,
            LinkedRepo.Resolve(project),
            // An agent driving this run reviews on its own provider and model; otherwise the ordinary
            // per-task routing cascade. Either way the toolset is narrowed by the review's depth,
            // which is the step the agent's route used to skip entirely.
            AiRouting.Bound(connection, Routed(connection, agent), Toolset(level, explorable: true)),
            // Always non-empty: blank means the built-in methodology. Project-specific rules ride
            // along in the review contexts instead.
            Settings.GetWorkspacePrompt(connection, project.WorkspaceId, "review_standard"),
            WorkspaceMcpStore.List(connection, project.WorkspaceId));
    }

    private static List<(string Name, string Content)> Contexts(IReadOnlyList<ReviewContext> contexts) =>
        [.. contexts.Where(c => c.Enabled).Select(c => (c.Name, c.Content))];

    /// <summary>The head commit of a ref, or empty when it does not resolve.</summary>
    /// <remarks>
    /// An unresolvable ref is deliberately not surfaced here. It resurfaces two steps later, as a hard
    /// failure from the branch diff, which is where 1.7.2 lets it through.
    /// </remarks>
    private static string ResolveSha(string repoPath, string headRef) =>
        Safely(() => Diff.ResolveSha(repoPath, headRef)) ?? "";

    /// <summary>Copies the workspace's enabled skills into a review's working directory.</summary>
    /// <remarks>
    /// Best-effort on both halves: reading the roster is wrapped, and the sync itself already
    /// swallows filesystem failures. A review that cannot see its skills is a worse review, not a
    /// failed one.
    /// </remarks>
    private static async Task SyncSkillsAsync(
        Database database, string workspaceId, string workingDirectory, CancellationToken cancellationToken)
    {
        var skills = await database
            .ReadAsync(c => SkillStore.List(c, workspaceId), cancellationToken)
            .ConfigureAwait(false);

        await Task.Run(
                () => SkillSync.TryRun(skills, SkillFiles.RootFor(workspaceId), workingDirectory), cancellationToken)
            .ConfigureAwait(false);
    }

    private static T? Safely<T>(Func<T?> work)
    {
        try
        {
            return work();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return default;
        }
    }

    /// <summary>Files the run in the activity list, best-effort.</summary>
    /// <remarks>
    /// Swallowed on purpose: unlike <c>act_on_pull_request</c>, where the history write is what makes
    /// an already-performed action visible and so is allowed to fail the command, a review's own text
    /// is the result. Losing its history row must not lose the review.
    /// </remarks>
    private static async Task RecordAsync(
        Database database,
        string jobId,
        string projectId,
        string label,
        string status,
        string? result,
        string? error,
        string meta,
        CancellationToken cancellationToken)
    {
        try
        {
            await database.WriteAsync(
                c => JobHistoryStore.Add(c, jobId, projectId, "pr-review", label, status, result, error, meta),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Best-effort by contract.
        }
    }
}
