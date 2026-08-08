using System.Text.Json;
using CodeFlow.Git;
using CodeFlow.Security;
using CodeFlow.Tests.TestVectors;
using Xunit;

namespace CodeFlow.Tests.Security;

/// <summary>
/// The pre-commit secret scanner, against the vectors.
/// See <c>docs/business-rules/10-security.md</c>, <c>SEC-008</c>–<c>SEC-013</c>.
/// </summary>
/// <remarks>
/// These are <c>kind: "vector"</c>, not scenarios: <c>scan_diff</c> is a pure function over an
/// already-parsed diff, so each case is replayed mechanically — the input is deserialised into the
/// same diff shape git produces, and the result is compared to the expected hits field for field.
/// No repository is involved.
/// </remarks>
public sealed class SecretScanTests
{
    private const string Vectors = "secret_scan.vectors.json";

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();

        foreach (var fixture in FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, Vectors)))
        {
            foreach (var testCase in fixture.Cases)
            {
                // Every case in this fixture is identified; a nameless one would be a fixture bug,
                // and failing here says so rather than silently skipping it.
                data.Add(testCase.Id ?? throw new InvalidOperationException($"a case in {Vectors} has no id"));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_extracted_vector_is_reproduced(string caseId)
    {
        var testCase = Find(caseId);

        var files = testCase.Input.GetProperty("files")
            .Deserialize(GitJsonContext.Default.IReadOnlyListFileDiffInfo)!;

        var hits = SecretScan.ScanDiff(files);

        Assert.Equal(
            Normalise(testCase.Expected),
            JsonSerializer.Serialize(hits, SecurityJsonContext.Default.IReadOnlyListSecretHit));
    }

    /// <summary>
    /// The rule order is behaviour: only the first match on a line is reported.
    /// </summary>
    /// <remarks>
    /// A line assigning a GitHub token to a variable called <c>token</c> matches both
    /// <c>github-token</c> and the generic <c>hardcoded-secret</c>. It has to be reported as the
    /// specific one — which is only true because the generic rule is declared last. Reordering the
    /// list silently downgrades a critical finding to a warning.
    /// </remarks>
    [Fact]
    public void A_line_matching_two_rules_is_reported_under_the_first_one()
    {
        var hits = SecretScan.ScanDiff([Added("const token = \"ghp_0123456789abcdefghijklmnopqrstuvwxyz\";")]);

        var hit = Assert.Single(hits);
        Assert.Equal("github-token", hit.Rule);
        Assert.Equal("critical", hit.Severity);
    }

    [Fact]
    public void At_most_one_hit_is_reported_per_line()
    {
        // Two distinct credentials on one line: the report stays readable at the cost of the second.
        var hits = SecretScan.ScanDiff([Added(
            "a = \"AKIAIOSFODNN7EXAMPLE\"; b = \"ghp_0123456789abcdefghijklmnopqrstuvwxyz\";")]);

        Assert.Equal("aws-access-key", Assert.Single(hits).Rule);
    }

    [Fact]
    public void A_removed_line_is_not_what_this_commit_introduces()
    {
        var removed = Added("const t = \"ghp_0123456789abcdefghijklmnopqrstuvwxyz\";") with
        {
            Hunks = [new DiffHunkInfo("@@", [new DiffLine("-", "const t = \"ghp_0123456789abcdefghijklmnopqrstuvwxyz\";", 42, null)])],
        };

        Assert.Empty(SecretScan.ScanDiff([removed]));
    }

    [Fact]
    public void A_file_with_no_new_path_falls_back_to_its_old_one_and_then_to_a_question_mark()
    {
        var deleted = new FileDiffInfo(
            "gone.ts",
            null,
            "deleted",
            [new DiffHunkInfo("@@", [new DiffLine("+", "key = AKIAIOSFODNN7EXAMPLE", null, 3)])]);

        Assert.Equal("gone.ts", Assert.Single(SecretScan.ScanDiff([deleted])).File);

        var nameless = deleted with { OldPath = null };
        Assert.Equal("?", Assert.Single(SecretScan.ScanDiff([nameless])).File);
    }

    [Fact]
    public void A_line_libgit2_gave_no_number_for_is_reported_as_zero()
    {
        var file = new FileDiffInfo(
            null,
            "config.ts",
            "modified",
            [new DiffHunkInfo("@@", [new DiffLine("+", "key = AKIAIOSFODNN7EXAMPLE", null, null)])]);

        Assert.Equal(0u, Assert.Single(SecretScan.ScanDiff([file])).Line);
    }

    /// <summary>
    /// The mask's short-value branch, at both sides of its boundary.
    /// </summary>
    /// <remarks>
    /// Six scalar values or fewer show nothing at all, and never fewer than three bullets — so a
    /// two-character value cannot be guessed from the width of its preview.
    /// </remarks>
    [Theory]
    [InlineData("ab", "•••")]
    [InlineData("abc", "•••")]
    [InlineData("abcdef", "••••••")]
    [InlineData("abcdefg", "abc••fg")]
    public void The_mask_hides_a_short_value_entirely(string value, string expected) =>
        Assert.Equal(expected, SecretScan.Mask(value));

    [Fact]
    public void The_mask_never_shows_more_than_sixteen_bullets()
    {
        // The cap obscures the length itself past 21 characters: a 40-character AWS key and a
        // 100-character one look the same.
        Assert.Equal($"abc{new string('•', 16)}yz", SecretScan.Mask(new string('x', 35).Insert(0, "abc")[..38] + "yz"));
        Assert.Equal(21, SecretScan.Mask(new string('x', 400)).Length);
    }

    /// <summary>
    /// The mask counts scalar values, because Unicode scalar counting does.
    /// </summary>
    /// <remarks>
    /// Counting UTF-16 units would take three characters where 1.7.2 takes three code
    /// points, and could split a surrogate pair — in a string whose only job is to be read.
    /// </remarks>
    [Fact]
    public void The_mask_measures_characters_and_not_utf16_units()
    {
        var value = string.Concat(Enumerable.Repeat("😀", 10));

        var masked = SecretScan.Mask(value);

        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 3)), masked[..6]);
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 2)), masked[^4..]);
        Assert.DoesNotContain(masked.EnumerateRunes().ToArray(), r => r.Value == 0xFFFD);
    }

    [Theory]
    [InlineData("${GITHUB_TOKEN}")]
    [InlineData("{{ token }}")]
    [InlineData("process.env.TOKEN")]
    [InlineData("os.environ['TOKEN']")]
    [InlineData("getenv(\"TOKEN\")")]
    [InlineData("your-token-here")]
    [InlineData("YOUR_TOKEN_HERE")]
    [InlineData("changeme123")]
    [InlineData("<put it here>")]
    [InlineData("xxxxxxxxxx")]
    [InlineData("TODO: fill in")]
    public void A_template_value_is_not_a_secret(string value) => Assert.True(SecretScan.IsPlaceholder(value));

    [Fact]
    public void The_placeholder_check_applies_only_to_the_generic_rule()
    {
        // AWS's own documentation key, which is what 1.7.2's test uses — and whose
        // lowercased form contains the "example" needle. A global placeholder check would filter it
        // out and quietly stop reporting the one credential everyone pastes by accident.
        var hits = SecretScan.ScanDiff([Added("key = AKIAIOSFODNN7EXAMPLE")]);

        Assert.Equal("aws-access-key", Assert.Single(hits).Rule);
    }

    private static FileDiffInfo Added(string content) =>
        new(null, "config.ts", "modified", [new DiffHunkInfo("@@", [new DiffLine("+", content, null, 42)])]);

    private static string Normalise(JsonElement expected) => JsonSerializer.Serialize(expected);

    private static FixtureCase Find(string caseId) =>
        FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, Vectors))
            .SelectMany(f => f.Cases)
            .Single(c => c.Id == caseId);
}
