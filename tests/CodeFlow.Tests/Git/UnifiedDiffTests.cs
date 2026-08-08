using CodeFlow.Git;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// Reading a provider's own diff text back into files.
/// </summary>
/// <remarks>
/// This exists because of `F-003`, found by the app reviewing its own pull request: the budget moved
/// out of `AiOperations` on the assumption that every diff arrives shaped by `GIT-031`, and the
/// link-review path — whose diff is text from GitHub or Azure, never libgit2 — quietly kept none.
/// </remarks>
public sealed class UnifiedDiffTests
{
    private const string TwoFiles =
        """
        diff --git a/src/auth.ts b/src/auth.ts
        index 1111111..2222222 100644
        --- a/src/auth.ts
        +++ b/src/auth.ts
        @@ -10,3 +10,4 @@ function login() {
           const user = find();
        +  if (!user) throw new Error("nope");
           return user;
         }
        diff --git a/README.md b/README.md
        index 3333333..4444444 100644
        --- a/README.md
        +++ b/README.md
        @@ -1,2 +1,2 @@
        -# Old
        +# New
         text
        """;

    [Fact]
    public void Every_file_in_the_diff_comes_back()
    {
        var files = UnifiedDiff.Parse(TwoFiles);

        Assert.Equal(2, files.Count);
        Assert.Equal(["src/auth.ts", "README.md"], files.Select(f => f.NewPath));
        Assert.All(files, f => Assert.NotEmpty(f.Hunks));
    }

    [Fact]
    public void The_git_prefixes_are_stripped_so_the_path_is_the_repositorys_own()
    {
        var file = UnifiedDiff.Parse(TwoFiles)[0];

        Assert.Equal("src/auth.ts", file.OldPath);
        Assert.Equal("src/auth.ts", file.NewPath);
    }

    [Fact]
    public void Lines_keep_their_origin_and_their_numbers()
    {
        var lines = UnifiedDiff.Parse(TwoFiles)[0].Hunks.Single().Lines;

        var added = Assert.Single(lines, l => l.Origin == "+");
        Assert.Contains("nope", added.Content, StringComparison.Ordinal);
        Assert.Equal(11, added.NewLineno);
    }

    [Fact]
    public void An_added_file_reports_no_old_path()
    {
        var files = UnifiedDiff.Parse(
            """
            diff --git a/src/new.ts b/src/new.ts
            new file mode 100644
            --- /dev/null
            +++ b/src/new.ts
            @@ -0,0 +1,2 @@
            +export const a = 1;
            +export const b = 2;
            """);

        var file = Assert.Single(files);
        Assert.Null(file.OldPath);
        Assert.Equal("src/new.ts", file.NewPath);
        Assert.Equal("added", file.Status);
    }

    [Fact]
    public void A_deleted_file_reports_no_new_path()
    {
        var files = UnifiedDiff.Parse(
            """
            diff --git a/src/gone.ts b/src/gone.ts
            deleted file mode 100644
            --- a/src/gone.ts
            +++ /dev/null
            @@ -1,1 +0,0 @@
            -export const a = 1;
            """);

        var file = Assert.Single(files);
        Assert.Equal("src/gone.ts", file.OldPath);
        Assert.Null(file.NewPath);
        Assert.Equal("deleted", file.Status);
    }

    [Fact]
    public void A_binary_file_is_reported_even_though_it_has_no_hunks()
    {
        // The banner is the whole signal — that the file changed — and it is worth keeping.
        var files = UnifiedDiff.Parse(
            """
            diff --git a/assets/icon.png b/assets/icon.png
            index 5555555..6666666 100644
            Binary files a/assets/icon.png and b/assets/icon.png differ
            """);

        var file = Assert.Single(files);
        Assert.Equal("assets/icon.png", file.NewPath);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void A_removed_line_that_looks_like_a_header_stays_content()
    {
        // F-006, found by this application reviewing the change that introduced this parser. A
        // deleted line whose content begins `-- ` renders as `--- …`, and reading it as a file
        // header stole the line out of the patch and split the file in two at that point. Diffs of
        // documentation hit this: `--- ` opens a YAML front-matter block and a Markdown rule.
        var files = UnifiedDiff.Parse(
            """
            diff --git a/docs/guide.md b/docs/guide.md
            --- a/docs/guide.md
            +++ b/docs/guide.md
            @@ -1,4 +1,4 @@
             # Guide
            --- old rule
            +++ new marker
             tail
            """);

        var file = Assert.Single(files);
        Assert.Equal("docs/guide.md", file.NewPath);

        var lines = file.Hunks.Single().Lines;
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.Origin == "-" && l.Content == "-- old rule");
        Assert.Contains(lines, l => l.Origin == "+" && l.Content == "++ new marker");
    }

    [Fact]
    public void Nothing_in_means_nothing_out()
    {
        Assert.Empty(UnifiedDiff.Parse(null));
        Assert.Empty(UnifiedDiff.Parse(""));
        Assert.Empty(UnifiedDiff.Parse("   \n  "));
    }

    public sealed class ShapingProviderText
    {
        [Fact]
        public void A_parsed_diff_gets_the_same_trimming_as_a_local_one()
        {
            var context = string.Join('\n', Enumerable.Range(1, 60).Select(i => $" line {i}"));
            var diff =
                "diff --git a/src/app.ts b/src/app.ts\n--- a/src/app.ts\n+++ b/src/app.ts\n"
                + "@@ -1,60 +1,61 @@\n" + context + "\n+added at the end\n";

            var rendered = PromptDiff.RenderText(diff, PromptDiff.DefaultBudgetChars);

            Assert.Contains("+added at the end", rendered, StringComparison.Ordinal);
            Assert.Contains("unchanged lines omitted", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(" line 1\n", rendered, StringComparison.Ordinal);
        }

        [Fact]
        public void A_parsed_diff_respects_the_budget()
        {
            var body = string.Join('\n', Enumerable.Range(1, 5_000).Select(i => $"+added {i}"));
            var diff = "diff --git a/src/big.ts b/src/big.ts\n--- a/src/big.ts\n+++ b/src/big.ts\n@@ -0,0 +1,5000 @@\n" + body;

            var rendered = PromptDiff.RenderText(diff, 8_000);

            Assert.True(rendered.Length <= 9_000, $"budget overrun: {rendered.Length}");
            Assert.Contains("truncated:", rendered, StringComparison.Ordinal);
        }

        [Fact]
        public void A_shape_the_parser_does_not_recognise_is_truncated_and_says_so()
        {
            // F-003's real lesson. An unfamiliar format must degrade to less content, never to no
            // limit — which is exactly what passing it through whole would have been.
            var opaque = new string('x', 200_000);

            var rendered = PromptDiff.RenderText(opaque, 8_000);

            Assert.True(rendered.Length < 20_000, $"unbounded: {rendered.Length}");
            Assert.Contains("truncated:", rendered, StringComparison.Ordinal);
        }

        [Fact]
        public void A_short_unrecognised_diff_is_passed_through_untouched()
        {
            Assert.Equal("some short text", PromptDiff.RenderText("some short text", 8_000));
        }

        [Fact]
        public void Nothing_renders_nothing()
        {
            Assert.Equal(string.Empty, PromptDiff.RenderText(null, 8_000));
            Assert.Equal(string.Empty, PromptDiff.RenderText("  ", 8_000));
        }
    }
}
