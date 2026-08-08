using System.Globalization;
using System.Text;

namespace CodeFlow.Git;

/// <summary>
/// How much of a change actually reached the model.
/// </summary>
/// <remarks>
/// Reported rather than assumed. The defect this whole class exists for was a prompt that lost
/// three quarters of a change and read as if it had not, and a count is the only way for the person
/// reading the review to tell "found nothing" from "never saw it".
/// </remarks>
/// <param name="Files">Files the change touches, whatever became of them here.</param>
/// <param name="Shown">Files whose diff reached the prompt, whole or truncated.</param>
/// <param name="Excluded">Files left out for carrying no reviewable signal — lock files, bundles.</param>
/// <param name="Omitted">Files left out for want of room in the budget.</param>
/// <param name="Truncated">Files shown in part.</param>
/// <param name="Carried">Files a re-review skipped because they had not changed since the last one.</param>
public sealed record DiffCoverage(
    int Files, int Shown, int Excluded, int Omitted, int Truncated, int Carried);

/// <summary>
/// Shapes a diff into the text a model is given.
/// </summary>
/// <remarks>
/// <para>
/// <b>The diffs this receives carry whole-file context on purpose</b> (<c>GIT-029</c>): the Changes
/// tab wants every line of the file with the edited ones highlighted, and that same computation
/// feeds the prompts. Handing a model the file when three lines either side would do is what made a
/// review of this repository's own commit render 468 KB where 73 KB carried the same information —
/// six and a half times the tokens, most of them lines nobody touched.
/// </para>
/// <para>
/// Worse than the cost was what the cost hid. The payload was cut to a character limit by
/// truncation, with no marker: the model received whole unchanged files at the front, nothing at all
/// from the back, and no way to know the difference. Roughly three quarters of the change never
/// reached it, and the review read as if it had. Everything here follows from that — trim to what
/// changed, drop what carries no reviewable signal, share the budget between files instead of
/// spending it on whichever came first, and <b>say so, in the text, every time something is left
/// out</b>.
/// </para>
/// </remarks>
public static class PromptDiff
{
    /// <summary>Lines kept either side of a changed line.</summary>
    /// <remarks>
    /// Three is git's own default for a reason: enough to see the statement a change sits in,
    /// little enough that the diff stays a diff.
    /// </remarks>
    public const int ContextLines = 3;

    /// <summary>
    /// A gap of this many unchanged lines or fewer is shown rather than declared.
    /// </summary>
    /// <remarks>
    /// Arithmetic, not taste. Declaring costs about thirty characters — and, because it splits one
    /// run into two, a second <c>@@</c> anchor of about twenty more. A line of code is around forty.
    /// So a single line is cheaper shown (≈40) than announced (≈50), and two are not (≈80 against
    /// ≈50). One it is. Without this the comment above would be describing a threshold the code
    /// never had, which is how it read before.
    /// </remarks>
    private const int DeclarableGap = 1;

    /// <summary>
    /// How many characters of rendered diff a prompt is given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Started as the number blunt truncation used to apply, now spent deliberately. Raised once the
    /// extract (<c>GIT-033</c>) removed the exploration it was paying for: the same review went from
    /// 512 849 billed tokens across forty-nine round trips to 115 702 in two, which is what buys the
    /// room. A pull request of fifty-two files was still cutting ten of them short at the old figure
    /// — and one of those cuts produced a false finding, from a model correctly reporting that it
    /// could not see a method we had trimmed away.
    /// </para>
    /// <para>
    /// It is a ceiling reached only by genuinely large changes: trimming alone puts an ordinary
    /// commit well under it.
    /// </para>
    /// </remarks>
    public const int DefaultBudgetChars = 250_000;

    /// <summary>
    /// The smallest share a file can be given before it is dropped whole instead.
    /// </summary>
    /// <remarks>
    /// A few hundred characters of a file is worse than an honest "not included": it reads as the
    /// whole change to that file while showing a fragment of it. Below this the file is named in the
    /// header rather than half-shown.
    /// </remarks>
    private const int MinimumFileShare = 1_200;

    /// <summary>
    /// Shapes a provider's own unified diff text, which never passed through libgit2.
    /// </summary>
    /// <remarks>
    /// A review reached by pasted link has no clone to diff, so the host hands back the diff as text
    /// (GitHub serves one; Azure builds one from blobs). That text used to go into the prompt whole
    /// and unbounded while the cloned path was being trimmed and budgeted — the same defect this
    /// class exists to fix, in the one route that did not come through it. Parsing it back into
    /// files puts both on the same road.
    /// <para>
    /// A diff whose shape the parser does not recognise is truncated and <b>said</b> rather than
    /// passed through: an unfamiliar format must degrade to less content, never to no limit.
    /// </para>
    /// </remarks>
    public static string RenderText(string? diffText, int budgetChars)
    {
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return string.Empty;
        }

        var files = UnifiedDiff.Parse(diffText);
        if (files.Count > 0)
        {
            return Render(files, budgetChars);
        }

        if (diffText.Length <= budgetChars)
        {
            return diffText;
        }

        return TruncateOnLineBoundary(diffText, budgetChars);
    }

    /// <summary>Renders a diff for a prompt, within a character budget.</summary>
    public static string Render(IReadOnlyList<FileDiffInfo> files, int budgetChars) =>
        Shape(files, budgetChars, []).Text;

    /// <summary>
    /// Renders a diff and reports what it left out, for the run's own stats line.
    /// </summary>
    /// <param name="carried">
    /// Paths a re-review is not showing because they are byte-identical to what the previous review
    /// already read. Their findings travel forward through <c>ReviewMemory.Reconcile</c>, which keeps
    /// a finding open when its file did not change — so what is dropped here is the cost of reading
    /// them again, not the findings themselves. Named in the notice like everything else omitted,
    /// because a model that cannot see a file must not be left believing it reviewed it.
    /// </param>
    public static (string Text, DiffCoverage Coverage) Shape(
        IReadOnlyList<FileDiffInfo> files, int budgetChars, IReadOnlyList<string> carried)
    {
        var kept = new List<(string Path, FileDiffInfo File)>();
        var skipped = new List<(string Path, string Reason)>();

        foreach (var file in files)
        {
            var path = PathOf(file);
            var reason = SkipReason(path);
            if (reason is null)
            {
                kept.Add((path, file));
            }
            else
            {
                skipped.Add((path, reason));
            }
        }

        var bodies = kept.Select(entry => RenderFile(entry.Path, entry.File)).ToArray();
        var costs = bodies.Select(body => body.Length).ToArray();

        // The notice costs characters too, and a budget that ignored it would be a budget that
        // overruns by exactly the amount it spends admitting it overran.
        //
        // Reserved against the files that will *actually* be named, which is why the shares are
        // computed twice. Reserving for every kept file instead — as this did — charged 120
        // characters a head for a list most of them never join: 200 files reserved a fifth of the
        // budget to say nothing, and a thousand consumed all of it, omitting every file for want of
        // room to admit it. The first pass is over the whole budget and so can only over-count the
        // omissions, never under-count them; the second is the one that is spent.
        var reserve = EstimateNoticeChars(skipped.Count, WouldNotFit(Share(costs, budgetChars), costs));
        var shares = Share(costs, Math.Max(0, budgetChars - reserve));

        var output = new StringBuilder();
        var omitted = new List<string>();
        var truncated = 0;

        for (var i = 0; i < bodies.Length; i++)
        {
            if (shares[i] >= costs[i])
            {
                output.Append(bodies[i]);
                continue;
            }

            if (shares[i] < MinimumFileShare)
            {
                omitted.Add(kept[i].Path);
                continue;
            }

            output.Append(TruncateOnLineBoundary(bodies[i], shares[i]));
            truncated++;
        }

        var coverage = new DiffCoverage(
            Files: files.Count + carried.Count,
            Shown: kept.Count - omitted.Count,
            Excluded: skipped.Count,
            Omitted: omitted.Count,
            Truncated: truncated,
            Carried: carried.Count);

        return (Notice(skipped, omitted, carried) + output, coverage);
    }

    /// <summary>What the model is told about its own blind spots, before the diff itself.</summary>
    private static string Notice(
        List<(string Path, string Reason)> skipped, List<string> omitted, IReadOnlyList<string> carried)
    {
        if (skipped.Count == 0 && omitted.Count == 0 && carried.Count == 0)
        {
            return string.Empty;
        }

        var notice = new StringBuilder("NOTE: this diff is not complete.\n");

        foreach (var (path, reason) in skipped)
        {
            notice.Append("  excluded (").Append(reason).Append("): ").Append(path).Append('\n');
        }

        foreach (var path in omitted)
        {
            notice.Append("  omitted (no room in this prompt): ").Append(path).Append('\n');
        }

        foreach (var path in carried)
        {
            notice.Append("  unchanged since the previous review, already reviewed: ").Append(path).Append('\n');
        }

        return notice.Append('\n').ToString();
    }

    /// <summary>A generous guess at the notice's size, so the budget can hold room for it.</summary>
    private static int EstimateNoticeChars(int skipped, int omitted) =>
        skipped + omitted == 0 ? 0 : 64 + ((skipped + omitted) * 120);

    /// <summary>How many files a set of shares would leave with too little to be worth showing.</summary>
    private static int WouldNotFit(int[] shares, int[] costs)
    {
        var count = 0;
        for (var i = 0; i < shares.Length; i++)
        {
            if (shares[i] < costs[i] && shares[i] < MinimumFileShare)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Divides a budget between files so no file starves because of where it happens to sit.
    /// </summary>
    /// <remarks>
    /// Water-filling, cheapest first: each file is offered an equal share of what is left, takes
    /// only what it needs, and hands the rest back to those still waiting. A hundred small files and
    /// one enormous one therefore all appear, with the enormous one carrying the truncation —
    /// where truncating by position would have shown the first file and none of the other hundred.
    /// </remarks>
    internal static int[] Share(int[] costs, int budget)
    {
        var shares = new int[costs.Length];
        var order = Enumerable.Range(0, costs.Length).OrderBy(i => costs[i]).ToArray();

        var remaining = (long)budget;
        var waiting = costs.Length;

        foreach (var i in order)
        {
            var offer = remaining / waiting;
            shares[i] = (int)Math.Min(costs[i], offer);
            remaining -= shares[i];
            waiting--;
        }

        return shares;
    }

    /// <summary>One file: its banner, then each run of changed lines with its own anchor.</summary>
    private static string RenderFile(string path, FileDiffInfo file)
    {
        var output = new StringBuilder();
        output.Append("--- ").Append(path).Append(" (").Append(file.Status).Append(")\n");

        foreach (var hunk in file.Hunks)
        {
            AppendTrimmedHunk(output, hunk);
        }

        return output.Append('\n').ToString();
    }

    /// <summary>
    /// Emits only what changed and its immediate surroundings, with the gaps declared.
    /// </summary>
    /// <remarks>
    /// The original <c>@@</c> header is not reproduced. With whole-file context there is one hunk
    /// per file spanning all of it, so its line counts describe the file rather than the change;
    /// each kept run gets its own header built from the line numbers the run actually starts at, so
    /// a finding can cite a line that exists.
    /// </remarks>
    private static void AppendTrimmedHunk(StringBuilder output, DiffHunkInfo hunk)
    {
        var lines = hunk.Lines;
        var keep = new bool[lines.Count];

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Origin is "+" or "-")
            {
                var from = Math.Max(0, i - ContextLines);
                var to = Math.Min(lines.Count - 1, i + ContextLines);
                for (var j = from; j <= to; j++)
                {
                    keep[j] = true;
                }
            }
        }

        // A gap narrower than its own declaration is absorbed rather than announced, which is what
        // makes the sentence above true. Doing it here, before anything is written, is what keeps
        // the two runs either side of an absorbed gap from being emitted as two: they are one run
        // now, and so carry one `@@` anchor between them instead of two.
        var index = 0;
        while (index < lines.Count)
        {
            if (keep[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < lines.Count && !keep[index])
            {
                index++;
            }

            if (index - start <= DeclarableGap)
            {
                for (var line = start; line < index; line++)
                {
                    keep[line] = true;
                }
            }
        }

        index = 0;
        while (index < lines.Count)
        {
            if (!keep[index])
            {
                var start = index;
                while (index < lines.Count && !keep[index])
                {
                    index++;
                }

                // A hunk with no changes at all has no run for a gap to sit between, and announcing
                // its whole length would be the only thing it said.
                if (index < lines.Count || start > 0)
                {
                    output.Append("~ ")
                        .Append((index - start).ToString(CultureInfo.InvariantCulture))
                        .Append(" unchanged lines omitted\n");
                }

                continue;
            }

            output.Append(Anchor(lines[index]));

            while (index < lines.Count && keep[index])
            {
                output.Append(lines[index].Origin).Append(lines[index].Content).Append('\n');
                index++;
            }
        }
    }

    /// <summary>The header for a run of kept lines, carrying where it really starts.</summary>
    private static string Anchor(DiffLine line)
    {
        var old = line.OldLineno?.ToString(CultureInfo.InvariantCulture) ?? "_";
        var updated = line.NewLineno?.ToString(CultureInfo.InvariantCulture) ?? "_";
        return $"@@ -{old} +{updated} @@\n";
    }

    /// <summary>Cuts at the last newline that fits, so a file never ends mid-line.</summary>
    private static string TruncateOnLineBoundary(string body, int limit)
    {
        var cut = body.LastIndexOf('\n', Math.Min(limit, body.Length) - 1);
        var head = cut > 0 ? body[..(cut + 1)] : body[..Math.Min(limit, body.Length)];
        return head + "~ truncated: this file's diff continues beyond what fits in this prompt\n\n";
    }

    private static string PathOf(FileDiffInfo file) => file.NewPath ?? file.OldPath ?? "?";

    /// <summary>
    /// Why a path carries nothing worth reviewing, or <see langword="null"/> when it does.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative. A lock file's thousand-line churn says nothing a reviewer acts on
    /// and crowds out the code that does, which is the case this exists for. Directories that are
    /// merely <em>usually</em> build output — <c>dist</c>, <c>build</c>, <c>out</c> — are not listed:
    /// they are ignored by git in the repositories that generate them, and in a repository where one
    /// holds real source, excluding it would lose a change. Whatever this does exclude is named in
    /// the notice, so the omission is visible rather than assumed.
    /// </remarks>
    internal static string? SkipReason(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

        if (LockFiles.Contains(name))
        {
            return "lock file";
        }

        if (name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase))
        {
            return "minified";
        }

        if (name.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
        {
            return "source map";
        }

        if (GeneratedMarkers.Any(marker => name.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            || name.Contains(".generated.", StringComparison.OrdinalIgnoreCase))
        {
            return "generated";
        }

        var segments = path.Split('/', '\\');
        return segments.Any(segment =>
            segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("vendor", StringComparison.OrdinalIgnoreCase))
            ? "vendored"
            : null;
    }

    private static readonly HashSet<string> LockFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json",
        "npm-shrinkwrap.json",
        "pnpm-lock.yaml",
        "yarn.lock",
        "packages.lock.json",
        "Cargo.lock",
        "go.sum",
        "poetry.lock",
        "Gemfile.lock",
        "composer.lock",
    };

    private static readonly string[] GeneratedMarkers =
    [
        ".g.cs",
        ".designer.cs",
        ".g.dart",
        ".freezed.dart",
        ".pb.go",
        "_pb2.py",
    ];
}
