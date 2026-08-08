using CodeFlow.Files;
using CodeFlow.Git;
using CodeFlow.Tests.Git;
using Xunit;

namespace CodeFlow.Tests.Files;

/// <summary>
/// Repo-wide listing, search and replace, against the scenarios.
/// See <c>docs/business-rules/11-files-search-terminal.md</c>, <c>FILE-007</c>–<c>FILE-011</c>.
/// </summary>
/// <remarks>
/// A real git repository, because the walk asks libgit2 what is ignored — a fake would be
/// asserting against this codebase's idea of gitignore rather than git's.
/// </remarks>
public sealed class SearchTests
{
    private const string Vectors = "search.vectors.json";

    /// <summary>The fixture five of the ten cases share.</summary>
    private static TempRepo SeededRepo()
    {
        var repo = new TempRepo();

        repo.Write("src/app.ts", "const answer = 42;\nexport { answer };\n");
        repo.Write("src/util.ts", "// the ANSWER helper\n");
        repo.Write("node_modules/pkg/index.js", "const answer = 1;\n");
        repo.Write("debug.log", "answer\n");
        repo.Write(".gitignore", "node_modules/\n*.log\n");

        return repo;
    }

    /// <summary>
    /// <c>DIVERGENCE-FILE-a</c>: ignored directories are pruned during the walk, never filtered out
    /// afterwards.
    /// </summary>
    [Fact]
    public void Listing_prunes_what_gitignore_excludes()
    {
        using var repo = SeededRepo();
        var expected = GitFixtures.Expected(Vectors, "lists-and-prunes-gitignored");

        var files = Search.ListFiles(repo.Path);

        Assert.Equal(expected.GetProperty("containsSrcAppTs").GetBoolean(), files.Contains("src/app.ts"));
        Assert.Equal(expected.GetProperty("containsGitignore").GetBoolean(), files.Contains(".gitignore"));
        Assert.Equal(
            expected.GetProperty("anyPathStartsWithNodeModules").GetBoolean(),
            files.Any(f => f.StartsWith("node_modules", StringComparison.Ordinal)));
        Assert.Equal(expected.GetProperty("containsDebugLog").GetBoolean(), files.Contains("debug.log"));
    }

    [Fact]
    public void Search_is_case_insensitive_by_default()
    {
        using var repo = SeededRepo();
        var expected = GitFixtures.Expected(Vectors, "case-insensitive-default");

        var outcome = Search.Find(repo.Path, "answer", new SearchOptions(), 100);

        Assert.Equal(Strings(expected, "hitPaths"), outcome.Hits.Select(h => h.Path).Distinct());
        Assert.DoesNotContain(outcome.Hits, h => h.Path == "debug.log");
        Assert.Equal(
            (uint)expected.GetProperty("srcAppTsLineNo").GetInt32(),
            outcome.Hits.First(h => h.Path == "src/app.ts").LineNo);
    }

    [Fact]
    public void A_case_sensitive_search_matches_only_the_case_it_was_given()
    {
        using var repo = SeededRepo();

        var outcome = Search.Find(repo.Path, "ANSWER", new SearchOptions { CaseSensitive = true }, 100);

        Assert.Equal(
            Strings(GitFixtures.Expected(Vectors, "case-sensitive-respects-case"), "hitPaths"),
            outcome.Hits.Select(h => h.Path).Distinct());
    }

    /// <summary>
    /// <c>FILE-008</c>: the caller's ceiling is the one cap that is reported. The other three —
    /// file count, file size, hits per file — are silent by design.
    /// </summary>
    [Fact]
    public void Hitting_the_ceiling_is_reported_rather_than_passed_off_as_the_whole_answer()
    {
        using var repo = SeededRepo();
        var expected = GitFixtures.Expected(Vectors, "truncation-flag");

        var outcome = Search.Find(repo.Path, "answer", new SearchOptions(), 1);

        Assert.Equal(expected.GetProperty("hitsLen").GetInt32(), outcome.Hits.Count);
        Assert.Equal(expected.GetProperty("truncated").GetBoolean(), outcome.Truncated);
    }

    [Fact]
    public void Whole_word_stops_matching_inside_longer_words()
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", "const set = 1;\nconst offset = 2;\n");
        var expected = GitFixtures.Expected(Vectors, "whole-word-boundary");

        Assert.Equal(
            Strings(expected, "looseHits"),
            Located(Search.Find(repo.Path, "set", new SearchOptions(), 50)));

        Assert.Equal(
            Strings(expected, "wholeWordHits"),
            Located(Search.Find(repo.Path, "set", new SearchOptions { WholeWord = true }, 50)));
    }

    [Fact]
    public void Regex_mode_reads_the_query_as_a_pattern_and_literal_mode_does_not()
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", "fn one() {}\nfn two() {}\nconst three = 3;\n");
        var expected = GitFixtures.Expected(Vectors, "regex-mode-vs-literal");

        const string query = @"fn \w+\(";

        Assert.Equal(
            Strings(expected, "regexModeHits"),
            Located(Search.Find(repo.Path, query, new SearchOptions { Regex = true }, 50)));

        // The same text escaped: no line contains that backslash-laden substring.
        Assert.Equal(
            Strings(expected, "literalModeHits"),
            Located(Search.Find(repo.Path, query, new SearchOptions(), 50)));
    }

    [Fact]
    public void An_unfinished_regex_reports_itself_instead_of_crashing()
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", "x\n");

        var failure = Assert.Throws<InvalidOperationException>(
            () => Search.Find(repo.Path, "foo(", new SearchOptions { Regex = true }, 50));

        // Only the prefix is a binding contract: 1.7.2 quotes the regex engine's own wording
        // after it, and .NET's cannot be the same words.
        Assert.StartsWith(
            GitFixtures.Expected(Vectors, "invalid-regex-error").GetProperty("error")
                .GetProperty("startsWith").GetString()!,
            failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>FILE-010</c>: two independent stages, and exclude can only remove.
    /// </summary>
    [Fact]
    public void Include_and_exclude_globs_narrow_the_scan_in_that_order()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.ts", "needle\n");
        repo.Write("src/a.test.ts", "needle\n");
        repo.Write("docs/a.md", "needle\n");
        var expected = GitFixtures.Expected(Vectors, "include-exclude-globs");

        // A bare pattern is rewritten to **/{pattern} and matches by file name at any depth. The
        // order is the walk's, sorted by file name: a.test.ts before a.ts.
        Assert.Equal(
            Strings(expected, "onlyTsIncludeHits"),
            Located(Search.Find(repo.Path, "needle", new SearchOptions { Include = "*.ts" }, 50)));

        Assert.Equal(
            Strings(expected, "excludeAfterIncludeHits"),
            Located(Search.Find(
                repo.Path,
                "needle",
                new SearchOptions { Include = "*.ts", Exclude = "*.test.ts" },
                50)));

        // A pattern containing '/' is matched against the whole repo-relative path instead.
        Assert.Equal(
            Strings(expected, "docsOnlyIncludeHits"),
            Located(Search.Find(repo.Path, "needle", new SearchOptions { Include = "docs/**" }, 50)));
    }

    /// <summary>
    /// <c>FILE-011</c>: every edit is planned before a byte is written, and the checkpoint taken in
    /// between is what makes a repo-wide replace undoable.
    /// </summary>
    [Fact]
    public void Replace_rewrites_matches_and_leaves_an_undo_behind()
    {
        using var repo = new TempRepo();
        repo.Write("src/a.ts", "const oldName = 1;\nuse(oldName);\n");
        repo.Write("src/b.ts", "nothing here\n");
        var expected = GitFixtures.Expected(Vectors, "replace-and-checkpoint-undo");

        var outcome = Search.ReplaceAll(repo.Path, "oldName", "newName", new SearchOptions(), null);

        Assert.Equal(expected.GetProperty("replacements").GetInt32(), outcome.Replacements);

        // src/b.ts has no match, so it is never touched — one file, not two.
        Assert.Equal(expected.GetProperty("files").GetInt32(), outcome.Files);
        Assert.Equal(expected.GetProperty("srcATsAfterReplace").GetString(), repo.Read("src/a.ts"));
        Assert.Equal(expected.GetProperty("checkpointIdPresent").GetBoolean(), outcome.CheckpointId is not null);

        Checkpoints.Restore(repo.Path, outcome.CheckpointId!);

        Assert.Equal(expected.GetProperty("srcATsAfterRestore").GetString(), repo.Read("src/a.ts"));
    }

    [Fact]
    public void Replace_can_be_scoped_to_one_file_and_can_use_capture_groups()
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", "call(1, 2);\n");
        repo.Write("b.ts", "call(3, 4);\n");
        var expected = GitFixtures.Expected(Vectors, "replace-scoped-with-capture-groups");

        var outcome = Search.ReplaceAll(
            repo.Path,
            @"call\((\d+), (\d+)\)",
            "call($2, $1)",
            new SearchOptions { Regex = true },
            "a.ts");

        Assert.Equal(expected.GetProperty("files").GetInt32(), outcome.Files);
        Assert.Equal(expected.GetProperty("aTsAfterReplace").GetString(), repo.Read("a.ts"));

        // b.ts matches the pattern too and is never even read, because only_path scopes the
        // candidate list before any matching happens.
        Assert.Equal(expected.GetProperty("bTsUnchanged").GetString(), repo.Read("b.ts"));
    }

    [Fact]
    public void An_empty_query_answers_nothing_without_touching_the_filesystem()
    {
        using var repo = SeededRepo();

        var found = Search.Find(repo.Path, "   ", new SearchOptions(), 100);
        Assert.Empty(found.Hits);
        Assert.False(found.Truncated);

        var replaced = Search.ReplaceAll(repo.Path, "", "x", new SearchOptions(), null);
        Assert.Equal(new ReplaceOutcome(0, 0, null), replaced);
    }

    [Fact]
    public void A_replace_that_matches_nothing_takes_no_checkpoint()
    {
        using var repo = SeededRepo();

        var outcome = Search.ReplaceAll(repo.Path, "nothing-matches-this", "x", new SearchOptions(), null);

        Assert.Equal(new ReplaceOutcome(0, 0, null), outcome);
        Assert.Empty(Checkpoints.List(repo.Path));
    }

    private static IEnumerable<string> Located(SearchOutcome outcome) =>
        outcome.Hits.Select(h => $"{h.Path}:{h.LineNo}");

    private static IEnumerable<string> Strings(System.Text.Json.JsonElement expected, string key) =>
        expected.GetProperty(key).EnumerateArray().Select(e => e.GetString()!);
}
