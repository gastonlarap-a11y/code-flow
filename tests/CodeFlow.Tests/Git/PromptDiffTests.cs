using CodeFlow.Git;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// What a model is actually shown of a change.
/// </summary>
/// <remarks>
/// This render had no tests, which is how it came to hand a model whole unchanged files and then
/// truncate three quarters of the change away without saying so. Two properties matter more than
/// any formatting detail here and are asserted throughout: <b>every changed line survives</b>, and
/// <b>nothing is left out silently</b>.
/// </remarks>
public sealed class PromptDiffTests
{
    [Fact]
    public void A_lone_change_in_a_long_file_brings_its_neighbours_and_not_the_file()
    {
        // The defect this exists for: whole-file context meant one edited line dragged the other
        // 199 along, six and a half times the tokens for the same information.
        var lines = new List<DiffLine>();
        for (var i = 1; i <= 200; i++)
        {
            lines.Add(new DiffLine(i == 100 ? "+" : " ", $"line {i}", i, i));
        }

        var rendered = Render(File("src/app.ts", lines));

        Assert.Contains("+line 100", rendered, StringComparison.Ordinal);
        Assert.Contains(" line 97", rendered, StringComparison.Ordinal);
        Assert.Contains(" line 103", rendered, StringComparison.Ordinal);

        // Three either side, and nothing beyond them.
        Assert.DoesNotContain(" line 96\n", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(" line 104\n", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lines_it_leaves_out_are_counted_where_they_were()
    {
        var lines = new List<DiffLine>();
        for (var i = 1; i <= 200; i++)
        {
            lines.Add(new DiffLine(i == 100 ? "+" : " ", $"line {i}", i, i));
        }

        var rendered = Render(File("src/app.ts", lines));

        // 96 before the kept run (1..96) and 104 after it (104..200) — declared, not just dropped.
        Assert.Contains("~ 96 unchanged lines omitted", rendered, StringComparison.Ordinal);
        Assert.Contains("~ 97 unchanged lines omitted", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_run_says_where_it_starts_so_a_finding_can_cite_a_real_line()
    {
        // The original `@@` header describes the whole file when context is whole-file, so it is
        // replaced per run. A model that cites line numbers needs numbers that mean something.
        var lines = new List<DiffLine>();
        for (var i = 1; i <= 60; i++)
        {
            lines.Add(new DiffLine(i == 50 ? "-" : " ", $"line {i}", i, i));
        }

        var rendered = Render(File("src/app.ts", lines));

        Assert.Contains("@@ -47 +47 @@", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_distant_changes_stay_two_runs_with_the_gap_between_them_declared()
    {
        var lines = new List<DiffLine>();
        for (var i = 1; i <= 100; i++)
        {
            lines.Add(new DiffLine(i is 10 or 90 ? "+" : " ", $"line {i}", i, i));
        }

        var rendered = Render(File("src/app.ts", lines));

        Assert.Contains("+line 10", rendered, StringComparison.Ordinal);
        Assert.Contains("+line 90", rendered, StringComparison.Ordinal);
        // 14..86 is one gap of 73 lines between the two runs.
        Assert.Contains("~ 73 unchanged lines omitted", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Adjacent_changes_are_one_run_rather_than_a_gap_of_nothing()
    {
        var lines = new List<DiffLine>();
        for (var i = 1; i <= 40; i++)
        {
            lines.Add(new DiffLine(i is 20 or 22 ? "+" : " ", $"line {i}", i, i));
        }

        var rendered = Render(File("src/app.ts", lines));

        Assert.Contains("+line 20", rendered, StringComparison.Ordinal);
        Assert.Contains("+line 22", rendered, StringComparison.Ordinal);
        Assert.Contains(" line 21", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("~ 0 unchanged lines omitted", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void An_added_file_is_shown_whole_because_all_of_it_is_new()
    {
        var lines = Enumerable.Range(1, 50)
            .Select(i => new DiffLine("+", $"line {i}", null, i))
            .ToList();

        var rendered = Render(File("src/new.ts", lines, status: "added"));

        Assert.Contains("+line 1", rendered, StringComparison.Ordinal);
        Assert.Contains("+line 50", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("unchanged lines omitted", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pnpm-lock.yaml", "lock file")]
    [InlineData("renderer/package-lock.json", "lock file")]
    [InlineData("go.sum", "lock file")]
    [InlineData("public/app.min.js", "minified")]
    [InlineData("dist/bundle.js.map", "source map")]
    [InlineData("src/Models.g.cs", "generated")]
    [InlineData("src/api.generated.ts", "generated")]
    [InlineData("node_modules/left-pad/index.js", "vendored")]
    public void Churn_with_no_reviewable_signal_is_named_rather_than_shown(string path, string reason)
    {
        var lines = Enumerable.Range(1, 500)
            .Select(i => new DiffLine("+", $"noise {i}", null, i))
            .ToList();

        var rendered = Render(File(path, lines));

        Assert.DoesNotContain("noise 1", rendered, StringComparison.Ordinal);
        Assert.Contains($"excluded ({reason}): {path}", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_that_is_only_usually_build_output_is_still_reviewed()
    {
        // `dist`/`build`/`out` are ignored by git where they are generated, and are real source
        // elsewhere. Excluding them by name would lose a change in the repositories where they
        // are not output — this repository stages into `shell/build/` itself.
        var rendered = Render(File("shell/build/thing.ts", [new DiffLine("+", "real code", null, 1)]));

        Assert.Contains("+real code", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("excluded", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tight_budget_still_shows_the_last_file_rather_than_only_the_first()
    {
        // Truncating the joined text by position spent the whole budget on whichever file came
        // first: the change at the end of the diff simply never reached the model.
        var big = Enumerable.Range(1, 4_000).Select(i => new DiffLine("+", $"big {i}", null, i)).ToList();
        var small = new List<DiffLine> { new("+", "the last change", null, 1) };

        var rendered = PromptDiff.Render([File("src/big.ts", big), File("src/small.ts", small)], 8_000);

        Assert.Contains("+the last change", rendered, StringComparison.Ordinal);
        Assert.Contains("src/big.ts", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_does_not_fit_is_cut_on_a_line_and_says_it_was_cut()
    {
        var big = Enumerable.Range(1, 4_000).Select(i => new DiffLine("+", $"big {i}", null, i)).ToList();

        var rendered = PromptDiff.Render([File("src/big.ts", big)], 8_000);

        Assert.Contains("truncated:", rendered, StringComparison.Ordinal);
        Assert.EndsWith("\n", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("+big 4000", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_room_left_is_named_instead_of_half_shown()
    {
        var big = Enumerable.Range(1, 4_000).Select(i => new DiffLine("+", $"big {i}", null, i)).ToList();
        var other = Enumerable.Range(1, 4_000).Select(i => new DiffLine("+", $"other {i}", null, i)).ToList();

        var rendered = PromptDiff.Render([File("src/a.ts", big), File("src/b.ts", other)], 1_500);

        Assert.Contains("omitted (no room in this prompt)", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_files_that_all_fit_are_all_shown()
    {
        // F-001, found by the app's own review of the change that introduced this file. The notice
        // reserve was taken over every kept file rather than over the files that would actually be
        // named, charging ~120 characters a head for a list almost none of them join. At this count
        // that reserved ~36 000 of 120 000 to say nothing; at a thousand files it consumed the
        // budget outright and omitted every one of them for want of room to admit it.
        var files = Enumerable.Range(1, 300)
            .Select(i => File($"src/file{i}.ts", [new DiffLine("+", $"change {i}", null, 1)]))
            .ToList();

        var rendered = PromptDiff.Render(files, PromptDiff.DefaultBudgetChars);

        Assert.DoesNotContain("NOTE:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated:", rendered, StringComparison.Ordinal);
        for (var i = 1; i <= 300; i++)
        {
            Assert.Contains($"+change {i}\n", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_single_line_gap_is_shown_rather_than_announced()
    {
        // F-002: the comment promised a threshold the code did not have, so a one-line gap cost
        // thirty characters to declare plus a second anchor, where showing it costs about forty.
        var lines = new List<DiffLine>();
        for (var i = 1; i <= 40; i++)
        {
            lines.Add(new DiffLine(i is 10 or 18 ? "+" : " ", $"line {i}", i, i));
        }

        // Context reaches 13 and resumes at 15, leaving exactly line 14 between the two runs.
        var rendered = Render(File("src/app.ts", lines));

        Assert.Contains(" line 14", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("~ 1 unchanged lines omitted", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Absorbing_a_gap_leaves_one_run_and_therefore_one_anchor()
    {
        var lines = new List<DiffLine>();
        for (var i = 1; i <= 40; i++)
        {
            lines.Add(new DiffLine(i is 10 or 18 ? "+" : " ", $"line {i}", i, i));
        }

        var rendered = Render(File("src/app.ts", lines));

        // Both changes sit in a single run now, so the interior anchor that split them is gone.
        Assert.Equal(1, rendered.Split("@@ -").Length - 1);
    }

    [Fact]
    public void A_gap_wide_enough_to_pay_for_itself_is_still_announced()
    {
        var lines = new List<DiffLine>();
        for (var i = 1; i <= 40; i++)
        {
            lines.Add(new DiffLine(i is 5 or 30 ? "+" : " ", $"line {i}", i, i));
        }

        var rendered = Render(File("src/app.ts", lines));

        // Context reaches line 8 and resumes at 27, so lines 9-26 sit between the two runs. Line 1
        // is a one-line leading gap and is absorbed, which is why the run starts there.
        Assert.Contains("~ 18 unchanged lines omitted", rendered, StringComparison.Ordinal);
        Assert.Contains(" line 1\n", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_diff_that_fits_carries_no_notice_at_all()
    {
        var rendered = Render(File("src/app.ts", [new DiffLine("+", "one line", null, 1)]));

        Assert.DoesNotContain("NOTE:", rendered, StringComparison.Ordinal);
        Assert.StartsWith("--- src/app.ts", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deleted_file_is_reported_under_the_path_it_had()
    {
        var file = new FileDiffInfo(
            "src/gone.ts", null, "deleted",
            [new DiffHunkInfo("@@ -1,1 +0,0 @@", [new DiffLine("-", "was here", 1, null)])]);

        var rendered = PromptDiff.Render([file], PromptDiff.DefaultBudgetChars);

        Assert.Contains("--- src/gone.ts (deleted)", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_binary_file_still_announces_that_it_changed()
    {
        // No hunks is how a binary arrives. The banner is the whole signal, and it is worth keeping.
        var file = new FileDiffInfo(null, "assets/icon.png", "modified", []);

        var rendered = PromptDiff.Render([file], PromptDiff.DefaultBudgetChars);

        Assert.Contains("--- assets/icon.png (modified)", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_changed_renders_nothing()
    {
        Assert.Equal(string.Empty, PromptDiff.Render([], PromptDiff.DefaultBudgetChars));
    }

    public sealed class SharingTheBudget
    {
        [Fact]
        public void Everything_fits_when_there_is_room_for_everything()
        {
            Assert.Equal([10, 20, 30], PromptDiff.Share([10, 20, 30], 100));
        }

        [Fact]
        public void What_a_small_file_does_not_need_goes_to_the_ones_that_do()
        {
            // Equal thirds would be 30 each; the file needing 5 hands back 25, so the two large
            // ones get more than an equal share rather than the surplus going unspent.
            var shares = PromptDiff.Share([5, 100, 100], 90);

            Assert.Equal(5, shares[0]);
            Assert.Equal(90, shares.Sum());
            Assert.True(shares[1] > 30 && shares[2] > 30);
        }

        [Fact]
        public void No_budget_means_no_shares_rather_than_a_crash()
        {
            Assert.Equal([0, 0], PromptDiff.Share([10, 20], 0));
        }

        [Fact]
        public void A_share_never_exceeds_what_the_file_costs()
        {
            var shares = PromptDiff.Share([1, 2, 3], 1_000);

            Assert.Equal([1, 2, 3], shares);
        }
    }

    private static string Render(FileDiffInfo file) =>
        PromptDiff.Render([file], PromptDiff.DefaultBudgetChars);

    private static FileDiffInfo File(string path, IReadOnlyList<DiffLine> lines, string status = "modified") =>
        new(path, path, status, [new DiffHunkInfo("@@ -1,1 +1,1 @@", lines)]);
}
