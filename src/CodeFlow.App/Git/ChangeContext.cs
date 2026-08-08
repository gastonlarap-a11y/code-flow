using System.Globalization;
using System.Text;

namespace CodeFlow.Git;

/// <summary>
/// The code around each change, extracted once, so the model does not go looking for it.
/// </summary>
/// <remarks>
/// <para>
/// Measured on a review of this repository: bounding the toolset removed <c>Bash</c> and the agent
/// replaced it with nineteen <c>Read</c>s and seven <c>Grep</c>s across twelve files — six minutes,
/// most of it spent reasoning over a context that grew with every one of them. It was not exploring
/// for the sake of it: a diff trimmed to three lines either side does not show the method a changed
/// line sits in, so the model went and opened it, once per file, guessing the range each time.
/// </para>
/// <para>
/// So the range is computed here instead — once, exactly, before the model is asked anything. What
/// it would have gone to fetch arrives with the question. The diff still travels alongside, because
/// it is the only thing that shows a <em>deleted</em> line; this is the other half of the same
/// picture, the new file as it now reads, with the changes marked in it.
/// </para>
/// <para>
/// It needs no filesystem access and no parser. The diffs it receives already carry whole-file
/// context (<c>GIT-029</c>), so every line of every changed file is in hand with its line number,
/// and a block is found by indentation — which is true of every language this reviews and of none
/// of them exactly. Where the guess fails it falls back to a window around the change and says as
/// much: a wider quote than needed costs tokens, a wrong one costs a finding.
/// </para>
/// </remarks>
public static class ChangeContext
{
    /// <summary>
    /// How many characters of extracted context a prompt is given.
    /// </summary>
    /// <remarks>
    /// Three fifths of the diff's own budget. The two are spent on the same change and overlap on
    /// the changed lines themselves, and this is the half that is redundant when the diff already
    /// showed enough — so when something has to give, it gives here. Both were raised together once
    /// the extract had paid for itself: a review that used to spend 512 849 billed tokens on
    /// exploring now spends 115 702 on being told, and the old ceilings were cutting ten files of a
    /// fifty-two-file change short.
    /// </remarks>
    public const int DefaultBudgetChars = 150_000;

    /// <summary>The most lines one block may quote before it is windowed instead.</summary>
    /// <remarks>
    /// The cap is what turns a wrong guess into a wide window rather than a copy of the file, and
    /// the guess is wrong in one predictable way: a change to a <em>single-line member</em> — a
    /// field, an expression-bodied method — has no block of its own, so the nearest enclosing
    /// declaration is the type, and the type is usually the file. Measured on this pull request
    /// before the cap was tightened, one edited field quoted all 170 lines of its class. A method
    /// long enough to lose out here is long enough to be a finding on its own.
    /// </remarks>
    private const int MaxBlockLines = 120;

    /// <summary>Lines quoted either side of a change when no enclosing block was found.</summary>
    private const int WindowLines = 20;

    /// <summary>How far above a declaration its doc comment and attributes are collected from.</summary>
    private const int AttachedLines = 20;

    /// <summary>Blocks closer together than this are quoted as one.</summary>
    /// <remarks>
    /// Two blocks separated by three lines cost more in headers than the lines between them cost
    /// shown, and reading as one contiguous quote is also more honest about what sits between them.
    /// </remarks>
    private const int JoinDistance = 3;

    /// <summary>The smallest share a file can take before it is named instead of quoted.</summary>
    private const int MinimumFileShare = 800;

    /// <summary>
    /// Quotes the enclosing declaration of every change, within a character budget.
    /// </summary>
    /// <remarks>
    /// Added and deleted files are left out: the diff already carries every line of those, and
    /// repeating them here would spend the budget saying the same thing twice. Whatever
    /// <see cref="PromptDiff.SkipReason"/> excludes from the diff is excluded here for the same
    /// reasons — it is the same judgement about the same file.
    /// </remarks>
    public static string Render(IReadOnlyList<FileDiffInfo> files, int budgetChars = DefaultBudgetChars)
    {
        var kept = new List<(string Path, string Body)>();

        foreach (var file in files)
        {
            var path = file.NewPath ?? file.OldPath ?? "?";
            if (file.Status is "added" or "deleted" || PromptDiff.SkipReason(path) is not null)
            {
                continue;
            }

            var body = RenderFile(path, Rows(file));
            if (body.Length > 0)
            {
                kept.Add((path, body));
            }
        }

        if (kept.Count == 0)
        {
            return string.Empty;
        }

        var costs = kept.Select(entry => entry.Body.Length).ToArray();
        var shares = PromptDiff.Share(costs, budgetChars);

        var output = new StringBuilder();
        var omitted = new List<string>();

        for (var i = 0; i < kept.Count; i++)
        {
            if (shares[i] >= costs[i])
            {
                output.Append(kept[i].Body);
            }
            else if (shares[i] < MinimumFileShare)
            {
                omitted.Add(kept[i].Path);
            }
            else
            {
                output.Append(TruncateOnLineBoundary(kept[i].Body, shares[i]));
            }
        }

        return output.Length == 0 ? string.Empty : Preamble(omitted) + output;
    }

    /// <summary>What the quote means, said once, and what it is missing.</summary>
    private static string Preamble(List<string> omitted)
    {
        var preamble = new StringBuilder(
            "CODE AROUND THE CHANGES\n"
            + "Each block below is the declaration enclosing a change, quoted whole from the head of\n"
            + "this pull request. The number is the line's real number in the file; '>' marks a line\n"
            + "this pull request added or modified. Deleted lines are not here — those are in the diff.\n");

        foreach (var path in omitted)
        {
            preamble.Append("  omitted (no room in this prompt): ").Append(path).Append('\n');
        }

        return preamble.Append('\n').ToString();
    }

    /// <summary>
    /// One file, as the head commit has it, with the changed lines flagged.
    /// </summary>
    /// <remarks>
    /// A removal has no line on the new side to flag, so it marks the line that took its place —
    /// otherwise a pure deletion would leave no trace at all in a quote of the file it was deleted
    /// from. One deletion at the very end of a file marks the last line instead.
    /// </remarks>
    private static List<Row> Rows(FileDiffInfo file)
    {
        var rows = new List<Row>();
        var deletionPending = false;

        foreach (var line in file.Hunks.SelectMany(hunk => hunk.Lines))
        {
            if (line.Origin == "-")
            {
                deletionPending = true;
                continue;
            }

            rows.Add(new Row(line.NewLineno ?? 0, line.Content, line.Origin == "+" || deletionPending));
            deletionPending = false;
        }

        if (deletionPending && rows.Count > 0)
        {
            rows[^1] = rows[^1] with { Changed = true };
        }

        return rows;
    }

    /// <summary>Every block of one file, merged where they touch.</summary>
    private static string RenderFile(string path, List<Row> rows)
    {
        var blocks = Merge(Blocks(rows));
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        var output = new StringBuilder();

        foreach (var (from, to) in blocks)
        {
            output.Append("── ").Append(path).Append(" lines ")
                .Append(rows[from].Number.ToString(CultureInfo.InvariantCulture)).Append('-')
                .Append(rows[to].Number.ToString(CultureInfo.InvariantCulture)).Append('\n');

            for (var i = from; i <= to; i++)
            {
                output.Append(rows[i].Changed ? "> " : "  ")
                    .Append(rows[i].Number.ToString(CultureInfo.InvariantCulture).PadLeft(5))
                    .Append(" | ").Append(rows[i].Text).Append('\n');
            }

            output.Append('\n');
        }

        return output.ToString();
    }

    /// <summary>One block per run of changed lines, expanded to the declaration around it.</summary>
    private static List<(int From, int To)> Blocks(List<Row> rows)
    {
        var blocks = new List<(int From, int To)>();
        var index = 0;

        while (index < rows.Count)
        {
            if (!rows[index].Changed)
            {
                index++;
                continue;
            }

            var start = index;
            var end = index;
            while (index < rows.Count)
            {
                if (rows[index].Changed)
                {
                    end = index;
                    index++;
                    continue;
                }

                // A couple of untouched lines between two edits are part of the same run: splitting
                // there would quote the same declaration twice.
                if (index - end > JoinDistance)
                {
                    break;
                }

                index++;
            }

            blocks.Add(Expand(rows, start, end));
        }

        return blocks;
    }

    /// <summary>
    /// Grows a run of changed lines out to the declaration that contains it.
    /// </summary>
    /// <remarks>
    /// Upwards to the first line indented less than the change — the signature, the class, the
    /// object key — unless the run already starts on a line that opens a block, in which case that
    /// line is the declaration and going further up would quote its parent instead. Downwards while
    /// the indentation stays inside, taking the closing delimiter with it.
    /// </remarks>
    private static (int From, int To) Expand(List<Row> rows, int start, int end)
    {
        var body = Depth(rows, start, end);
        var header = Opens(rows, start) ? start : Above(rows, start, body);
        var depth = Indent(rows[header].Text);
        if (depth == Blank)
        {
            depth = 0;
        }

        var from = Attached(rows, header, depth);
        var to = Below(rows, end, depth);

        return to - from + 1 > MaxBlockLines ? Window(rows, start, end) : (from, to);
    }

    /// <summary>The shallowest indentation the run itself sits at.</summary>
    private static int Depth(List<Row> rows, int start, int end)
    {
        var depth = Blank;
        for (var i = start; i <= end; i++)
        {
            depth = Math.Min(depth, Indent(rows[i].Text));
        }

        return depth == Blank ? 0 : depth;
    }

    /// <summary>The nearest line above the run that is indented less than it.</summary>
    /// <remarks>
    /// A lone <c>{</c> is not a declaration, it is the punctuation of the one above it — and this
    /// codebase writes them that way, so stopping there would quote a block whose first line does
    /// not say what the block is.
    /// </remarks>
    private static int Above(List<Row> rows, int start, int body)
    {
        for (var i = start - 1; i >= 0; i--)
        {
            var indent = Indent(rows[i].Text);
            if (indent == Blank || indent >= body)
            {
                continue;
            }

            return IsOpening(rows[i].Text.TrimStart()) ? Declaration(rows, i) : i;
        }

        return start;
    }

    /// <summary>The line an opening delimiter belongs to.</summary>
    private static int Declaration(List<Row> rows, int brace)
    {
        for (var i = brace - 1; i >= 0; i--)
        {
            if (Indent(rows[i].Text) != Blank)
            {
                return i;
            }
        }

        return brace;
    }

    /// <summary>The declaration's own doc comment, attributes or decorators, when it has any.</summary>
    private static int Attached(List<Row> rows, int header, int depth)
    {
        var from = header;

        for (var i = header - 1; i >= 0 && header - i <= AttachedLines; i--)
        {
            var text = rows[i].Text.TrimStart();
            if (text.Length == 0 || Indent(rows[i].Text) != depth || !IsAttachment(text))
            {
                break;
            }

            from = i;
        }

        return from;
    }

    /// <summary>Down to where the declaration closes, blank tail lines dropped.</summary>
    private static int Below(List<Row> rows, int end, int depth)
    {
        var to = end;

        for (var i = end + 1; i < rows.Count; i++)
        {
            var indent = Indent(rows[i].Text);

            // The brace that opens the declaration sits at its own indentation, so the body starts
            // one line further down than the depth test alone would find.
            if (indent == Blank || indent > depth || IsOpening(rows[i].Text.TrimStart()))
            {
                to = i;
                continue;
            }

            if (IsClosing(rows[i].Text.TrimStart()))
            {
                to = i;
            }

            break;
        }

        while (to > end && Indent(rows[to].Text) == Blank)
        {
            to--;
        }

        return to;
    }

    /// <summary>What is quoted when the block around a change turns out to be the whole file.</summary>
    private static (int From, int To) Window(List<Row> rows, int start, int end) =>
        (Math.Max(0, start - WindowLines), Math.Min(rows.Count - 1, end + WindowLines));

    /// <summary>Blocks that touch or overlap, quoted as one.</summary>
    private static List<(int From, int To)> Merge(List<(int From, int To)> blocks)
    {
        var merged = new List<(int From, int To)>();

        foreach (var block in blocks.OrderBy(b => b.From))
        {
            if (merged.Count > 0 && block.From <= merged[^1].To + JoinDistance)
            {
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, block.To));
                continue;
            }

            merged.Add(block);
        }

        return merged;
    }

    /// <summary>
    /// Whether a line starts a block, whichever side of it the brace is written on.
    /// </summary>
    /// <remarks>
    /// A line holding nothing but an opening delimiter belongs to the line above it — this
    /// codebase's own style puts <c>{</c> there — so it is skipped over rather than compared
    /// against, and a signature reads as opening a block in both conventions and in the languages
    /// that have no brace at all.
    /// </remarks>
    private static bool Opens(List<Row> rows, int index)
    {
        var indent = Indent(rows[index].Text);
        if (indent == Blank)
        {
            return false;
        }

        for (var i = index + 1; i < rows.Count; i++)
        {
            var text = rows[i].Text.TrimStart();
            if (text.Length == 0 || IsOpening(text))
            {
                continue;
            }

            return Indent(rows[i].Text) > indent;
        }

        return false;
    }

    private static bool IsOpening(string trimmed) => trimmed is "{" or "(" or "[" or "=>" or "{{";

    private static bool IsClosing(string trimmed) =>
        trimmed.StartsWith('}') || trimmed.StartsWith(')') || trimmed.StartsWith(']')
        || trimmed is "end" or "END" or "fi" or "done";

    private static bool IsAttachment(string trimmed) =>
        trimmed.StartsWith("///", StringComparison.Ordinal)
        || trimmed.StartsWith("//", StringComparison.Ordinal)
        || trimmed.StartsWith("/*", StringComparison.Ordinal)
        || trimmed.StartsWith('*')
        || trimmed.StartsWith('#')
        || trimmed.StartsWith('[')
        || trimmed.StartsWith('@')
        || trimmed.StartsWith("--", StringComparison.Ordinal);

    /// <summary>A line's indentation in spaces, with a tab worth four.</summary>
    private static int Indent(string text)
    {
        var indent = 0;

        foreach (var character in text)
        {
            switch (character)
            {
                case ' ':
                    indent++;
                    break;
                case '\t':
                    indent += 4;
                    break;
                default:
                    return indent;
            }
        }

        return Blank;
    }

    /// <summary>A line with nothing on it has no indentation to compare, rather than none.</summary>
    private const int Blank = int.MaxValue;

    private static string TruncateOnLineBoundary(string body, int limit)
    {
        var cut = body.LastIndexOf('\n', Math.Min(limit, body.Length) - 1);
        var head = cut > 0 ? body[..(cut + 1)] : body[..Math.Min(limit, body.Length)];
        return head + "~ truncated: this file's context continues beyond what fits in this prompt\n\n";
    }

    private sealed record Row(int Number, string Text, bool Changed);
}
