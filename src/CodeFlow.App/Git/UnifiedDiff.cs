namespace CodeFlow.Git;

/// <summary>
/// Splits a whole unified diff into the per-file shape the rest of this feature speaks.
/// </summary>
/// <remarks>
/// <para>
/// A pull-request host hands back a diff as text: GitHub serves one directly, and Azure builds one
/// from blobs because it has no diff endpoint. Every diff computed locally arrives as
/// <see cref="FileDiffInfo"/> already, so nothing needed this until a review could reach the model
/// without a clone — and that path then carried the provider's text straight into the prompt, whole,
/// while the other was being trimmed and budgeted. Parsing it here is what lets both take the same
/// road through <see cref="PromptDiff"/>.
/// </para>
/// <para>
/// Deliberately forgiving about what it does not need. Mode changes, index lines, binary notices and
/// rename headers are read for the paths they carry and otherwise skipped: the destination is a
/// prompt, not <c>git apply</c>. What it must not do is silently return nothing for a diff that
/// plainly has content — the caller treats an empty parse as "fall back and truncate, loudly".
/// </para>
/// </remarks>
internal static class UnifiedDiff
{
    /// <summary>Reads a unified diff into one entry per file, hunks and line numbers included.</summary>
    public static IReadOnlyList<FileDiffInfo> Parse(string? diffText)
    {
        var files = new List<FileDiffInfo>();
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return files;
        }

        string? oldPath = null;
        string? newPath = null;
        var body = new List<string>();

        void Flush()
        {
            if (oldPath is null && newPath is null)
            {
                return;
            }

            files.Add(new FileDiffInfo(
                oldPath,
                newPath,
                Status(oldPath, newPath),
                UnifiedPatch.Hunks(body.Count == 0 ? null : string.Join('\n', body))));

            oldPath = null;
            newPath = null;
            body.Clear();
        }

        foreach (var line in diffText.Split('\n'))
        {
            // `diff --git` opens a file. Its own paths are read only as a fallback: the `---`/`+++`
            // pair below is authoritative, and a rename reports different ones on each side.
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                (oldPath, newPath) = FromGitHeader(line);
                continue;
            }

            // Only outside a hunk. Inside one, `--- x` is a *deleted line* whose content begins
            // `-- `, and `+++ x` an added line beginning `++ ` — content, not headers. Reading them
            // as headers stole the line out of the patch and split the file in two at that point.
            // Found by this application reviewing the change that introduced this parser (`F-006`).
            if (body.Count == 0)
            {
                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    oldPath = Path(line[4..]);
                    continue;
                }

                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    newPath = Path(line[3..].TrimStart());
                    continue;
                }
            }

            // Everything from the first `@@` onwards is the patch `UnifiedPatch` already knows how to
            // read; the headers between files are not, and would be read as context lines.
            if (line.StartsWith("@@", StringComparison.Ordinal) || body.Count > 0)
            {
                body.Add(line);
            }
        }

        Flush();
        return files;
    }

    /// <summary>
    /// The two paths a <c>diff --git a/x b/y</c> line carries, when they can be told apart.
    /// </summary>
    /// <remarks>
    /// A path containing a space makes this ambiguous, which is why git also emits <c>---</c> and
    /// <c>+++</c> and why those win. This is the fallback for a diff that omits them — a pure mode
    /// change, or a binary file.
    /// </remarks>
    private static (string?, string?) FromGitHeader(string line)
    {
        var rest = line["diff --git ".Length..].Trim();
        var split = rest.IndexOf(" b/", StringComparison.Ordinal);
        return split < 0
            ? (null, null)
            : (Path(rest[..split]), Path(rest[(split + 1)..]));
    }

    /// <summary>Strips git's <c>a/</c> and <c>b/</c> prefixes, and recognises "absent".</summary>
    private static string? Path(string raw)
    {
        var path = raw.Trim();

        // Git's own name for a side that does not exist: an added file has no old path, a deleted
        // one no new path. Returning null is what makes `Status` able to tell them apart.
        if (path is "/dev/null" or "")
        {
            return null;
        }

        // A tab separates the path from a timestamp in a diff that carries one.
        var tab = path.IndexOf('\t', StringComparison.Ordinal);
        if (tab >= 0)
        {
            path = path[..tab];
        }

        return path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;
    }

    /// <summary>
    /// The label `PromptDiff` prints in a file's banner.
    /// </summary>
    /// <remarks>
    /// The same four words libgit2's diffs use, so a prompt reads identically whichever side built
    /// it. Renames are reported as modifications: the parse can see the paths differ, but not
    /// whether git called it a rename or the file simply moved, and guessing would put a word in the
    /// prompt that the other path never uses for the same situation.
    /// </remarks>
    private static string Status(string? oldPath, string? newPath) => (oldPath, newPath) switch
    {
        (null, not null) => "added",
        (not null, null) => "deleted",
        _ => "modified",
    };
}
