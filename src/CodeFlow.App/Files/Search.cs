using System.Text;
using System.Text.RegularExpressions;
using CodeFlow.Git;

namespace CodeFlow.Files;

/// <summary>
/// Repo-wide file listing and content search — what "go to file" and "find in project" run on.
/// See <c>FILE-007</c>–<c>FILE-011</c>.
/// </summary>
public static class Search
{
    /// <summary>
    /// Files above this are skipped by content search: minified bundles and checked-in data dumps
    /// are never what someone is looking for, and reading them is most of the cost.
    /// </summary>
    private const long MaxSearchFileBytes = 1024 * 1024;

    /// <summary>
    /// Long lines (a minified bundle that slipped past the size check) are cut before crossing the
    /// IPC boundary — the UI shows one line per hit and cannot render a 200 KB one anyway.
    /// </summary>
    private const int MaxLineChars = 400;

    private const int MaxHitsPerFile = 20;

    /// <summary>Every non-ignored file in the repo, repo-relative and sorted.</summary>
    public static IReadOnlyList<string> ListFiles(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);

        return RepoWalk.Files(repo, RepoWalk.WorkingDirectory(repo));
    }

    /// <summary>Searches the repo's text files, honouring every toggle in <see cref="SearchOptions"/>.</summary>
    public static SearchOutcome Find(string repoPath, string query, SearchOptions options, int maxResults)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return new SearchOutcome([], false);
        }

        var matcher = BuildMatcher(query, options);
        var include = GlobSet.Build(options.Include);
        var exclude = GlobSet.Build(options.Exclude);

        using var repo = RepoStatus.Open(repoPath);
        var root = RepoWalk.WorkingDirectory(repo);

        var hits = new List<SearchHit>();

        foreach (var rel in RepoWalk.Files(repo, root))
        {
            if (hits.Count >= maxResults)
            {
                return new SearchOutcome(hits, true);
            }

            if (!PassesFilters(rel, include, exclude))
            {
                continue;
            }

            if (ReadTextFile(Path.Combine(root, rel)) is not { } text)
            {
                continue;
            }

            var inFile = 0;
            var lineNo = 0u;

            foreach (var line in Lines(text))
            {
                lineNo++;

                if (inFile >= MaxHitsPerFile || hits.Count >= maxResults)
                {
                    break;
                }

                if (matcher.IsMatch(line))
                {
                    inFile++;
                    hits.Add(new SearchHit(rel, lineNo, TruncateLine(line)));
                }
            }
        }

        return new SearchOutcome(hits, hits.Count >= maxResults);
    }

    /// <summary>
    /// Rewrites every match with <paramref name="replacement"/> across the repo, or within
    /// <paramref name="onlyPath"/> when given (<c>FILE-011</c>).
    /// </summary>
    /// <remarks>
    /// A checkpoint is taken first: this writes to files the user may not even have open, and a
    /// project-wide replace with no undo is a trap. <c>$1</c>-style group references work when the
    /// query is a regex, the same as in the editors people are used to.
    /// </remarks>
    public static ReplaceOutcome ReplaceAll(
        string repoPath,
        string query,
        string replacement,
        SearchOptions options,
        string? onlyPath)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return new ReplaceOutcome(0, 0, null);
        }

        var matcher = BuildMatcher(query, options);
        var include = GlobSet.Build(options.Include);
        var exclude = GlobSet.Build(options.Exclude);

        using var repo = RepoStatus.Open(repoPath);
        var root = RepoWalk.WorkingDirectory(repo);

        // Every edit is computed before a single byte is written, so a file that fails to read
        // halfway through cannot leave the tree half-replaced.
        var planned = new List<(string Rel, string Content, int Count)>();

        foreach (var rel in RepoWalk.Files(repo, root))
        {
            if (onlyPath is not null && rel != onlyPath)
            {
                continue;
            }

            if (!PassesFilters(rel, include, exclude))
            {
                continue;
            }

            if (ReadTextFile(Path.Combine(root, rel)) is not { } text)
            {
                continue;
            }

            var count = matcher.Count(text);
            if (count == 0)
            {
                continue;
            }

            var replaced = matcher.Replace(text, replacement);
            if (replaced != text)
            {
                planned.Add((rel, replaced, count));
            }
        }

        if (planned.Count == 0)
        {
            return new ReplaceOutcome(0, 0, null);
        }

        var checkpointId = TryCheckpoint(repoPath);
        var replacements = 0;
        var written = 0;

        foreach (var (rel, content, count) in planned)
        {
            try
            {
                File.WriteAllText(Path.Combine(root, rel), content);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Aborts here, leaving the files written before this one written: rolling those
                // back is what the checkpoint is for, not an in-process undo.
                throw new InvalidOperationException($"{rel}: {e.Message}");
            }

            replacements += count;
            written++;
        }

        return new ReplaceOutcome(replacements, written, checkpointId);
    }

    /// <summary>Compiles the query into the one matcher both search and replace run on.</summary>
    /// <remarks>
    /// <para>
    /// Everything funnels through one regular expression, including plain-text search: escaping a
    /// literal is cheaper than maintaining two matching paths that have to agree about case folding
    /// and word boundaries. The composition order is load-bearing (<c>FILE-009</c>) — escape, then
    /// wrap for whole-word, then prefix the case-insensitivity flag — which is what makes
    /// <c>wholeWord</c> apply to literal queries too.
    /// </para>
    /// <para>
    /// <see cref="RegexOptions.NonBacktracking"/> is required, not a precaution. It guarantees
    /// linear time and, for that reason, rejects backreferences and lookaround. The default engine
    /// accepts those and can hang for minutes on a pathological pattern — and this pattern comes
    /// straight from a text box the user types into.
    /// </para>
    /// </remarks>
    private static Regex BuildMatcher(string query, SearchOptions options)
    {
        var body = options.Regex ? query : Regex.Escape(query);
        body = options.WholeWord ? $@"\b(?:{body})\b" : body;
        var pattern = options.CaseSensitive ? body : $"(?i){body}";

        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
        catch (Exception e) when (e is RegexParseException or NotSupportedException or ArgumentException)
        {
            // A half-typed regex is the normal state of the box while someone types, so this reads
            // as feedback rather than as a crash.
            var detail = e.Message.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;

            throw new InvalidOperationException($"invalid regular expression: {detail}");
        }
    }

    /// <summary>
    /// Include first, then exclude — two independent stages, and exclude always wins
    /// (<c>FILE-010</c>).
    /// </summary>
    private static bool PassesFilters(string path, GlobSet? include, GlobSet? exclude) =>
        (include is null || include.IsMatch(path)) && (exclude is null || !exclude.IsMatch(path));

    /// <summary>Reads a file if it is text and small enough to be worth searching.</summary>
    /// <remarks>
    /// Decoding is deliberately lossy here, unlike <see cref="FileOps.ReadFileText"/>: the
    /// reference uses <c>from_utf8_lossy</c>, so a file with one bad byte is still searched with
    /// U+FFFD standing in for it rather than being skipped.
    /// </remarks>
    private static string? ReadTextFile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxSearchFileBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);

            return LooksBinary(bytes) ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a byte slice looks like binary content. A NUL byte is the same heuristic
    /// <c>grep</c> uses, and it is enough to keep images and compiled artifacts out of text search
    /// results.
    /// </summary>
    private static bool LooksBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(8192, bytes.Length)).Contains((byte)0);

    /// <summary>Splits text into lines, breaking on <c>\n</c> and <c>\r\n</c> only.</summary>
    /// <remarks>
    /// Not <see cref="MemoryExtensions.EnumerateLines"/>, which also breaks on a lone <c>\r</c>,
    /// U+0085, U+2028 and U+2029. Any file containing one of those would get line numbers the
    /// renderer disagrees with, and a hit's line number is what the editor jumps to.
    /// A trailing newline yields no final empty line.
    /// </remarks>
    internal static IEnumerable<string> Lines(string text)
    {
        var start = 0;

        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);

            if (newline < 0)
            {
                yield return text[start..];
                yield break;
            }

            var stop = newline > start && text[newline - 1] == '\r' ? newline - 1 : newline;
            yield return text[start..stop];

            start = newline + 1;
        }
    }

    /// <summary>Cuts a line to <see cref="MaxLineChars"/> and marks that it was cut.</summary>
    /// <remarks>
    /// The cap counts Unicode scalar values, never UTF-16 units. Counting UTF-16
    /// units instead would halve the limit for a line of emoji and could cut a surrogate pair in
    /// two, producing a string that is not valid UTF-16 to send over the wire.
    /// </remarks>
    internal static string TruncateLine(string line)
    {
        var trimmed = line.TrimEnd('\r', '\n');

        var index = 0;
        var count = 0;

        foreach (var rune in trimmed.EnumerateRunes())
        {
            // Reaching the cap with a rune still to go is what makes the line too long; a line of
            // exactly MaxLineChars falls out of the loop untouched.
            if (count == MaxLineChars)
            {
                return string.Concat(trimmed.AsSpan(0, index), "…");
            }

            index += rune.Utf16SequenceLength;
            count++;
        }

        return trimmed;
    }

    /// <summary>
    /// Snapshots the tree before the writes, best-effort.
    /// </summary>
    /// <remarks>
    /// 1.7.2's <c>.ok()</c>: a snapshot that fails leaves <c>checkpoint_id</c> null and the
    /// replace goes ahead regardless. Refusing to replace because the undo could not be recorded
    /// would be a different command.
    /// </remarks>
    private static string? TryCheckpoint(string repoPath)
    {
        try
        {
            return Checkpoints.Create(repoPath, "replace-all");
        }
        catch (Exception e) when (e is LibGit2Sharp.LibGit2SharpException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
