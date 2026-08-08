using CodeFlow.Git;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// The code a review is handed instead of going to read for itself.
/// </summary>
/// <remarks>
/// Two properties matter more than the formatting: <b>every changed line is quoted and marked</b>,
/// and <b>the block around it is the declaration, not the file</b>. The first is what makes the
/// extract a substitute for opening the file; the second is what keeps it cheaper than doing so.
/// </remarks>
public sealed class ChangeContextTests
{
    /// <summary>A method with an edit in its body, inside a class, inside a namespace.</summary>
    private static readonly string[] CSharp =
    [
        "namespace CodeFlow.Sample;",
        "",
        "public static class Rates",
        "{",
        "    /// <summary>The one everybody calls.</summary>",
        "    public static decimal Convert(decimal amount, decimal rate)",
        "    {",
        "        var converted = amount * rate;",
        "        return decimal.Round(converted, 2);",
        "    }",
        "",
        "    public static decimal Untouched(decimal amount) => amount;",
        "}",
    ];

    [Fact]
    public void A_change_brings_the_whole_declaration_it_sits_in()
    {
        var rendered = Render("src/Rates.cs", CSharp, changed: [8]);

        // The signature and its doc comment, the body, and the brace that closes it.
        Assert.Contains("public static decimal Convert(decimal amount, decimal rate)", rendered, StringComparison.Ordinal);
        Assert.Contains("The one everybody calls.", rendered, StringComparison.Ordinal);
        Assert.Contains("return decimal.Round(converted, 2);", rendered, StringComparison.Ordinal);

        // Not the sibling method, and not the class banner: that is the difference between quoting
        // the declaration and quoting the file.
        Assert.DoesNotContain("Untouched", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_lines_the_change_touched_are_marked()
    {
        var rendered = Render("src/Rates.cs", CSharp, changed: [8]);

        Assert.Contains(">     8 |         var converted = amount * rate;", rendered, StringComparison.Ordinal);
        Assert.Contains("      9 |         return decimal.Round(converted, 2);", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_a_signature_quotes_that_method_and_not_its_class()
    {
        // The line the change sits on already opens a block, so walking further up would hand back
        // the whole class — which for a real file is the whole file.
        var rendered = Render("src/Rates.cs", CSharp, changed: [6]);

        Assert.Contains("public static decimal Convert", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("public static class Rates", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_outside_every_declaration_is_quoted_too()
    {
        // An import, a top-level constant, a namespace line: nothing the pull request touched may
        // go unquoted just because no block contains it.
        var rendered = Render("src/Rates.cs", CSharp, changed: [1]);

        Assert.Contains("namespace CodeFlow.Sample;", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deletion_marks_the_line_that_took_its_place()
    {
        // A removed line has no line on the new side to flag, and a pure deletion would otherwise
        // leave no trace at all in a quote of the file it was deleted from.
        var lines = new List<DiffLine>
        {
            new(" ", "function total(items) {", 1, 1),
            new("-", "  const legacy = 0;", 2, null),
            new(" ", "  return items.length;", 2, 2),
            new(" ", "}", 3, 3),
        };

        var rendered = ChangeContext.Render([new FileDiffInfo("a.js", "a.js", "modified", [new DiffHunkInfo("@@", lines)])]);

        Assert.Contains(">     2 |   return items.length;", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void An_indentation_language_closes_its_block_by_dedenting()
    {
        string[] python =
        [
            "import os",
            "",
            "def convert(amount, rate):",
            "    converted = amount * rate",
            "    return round(converted, 2)",
            "",
            "def untouched(amount):",
            "    return amount",
        ];

        var rendered = Render("app.py", python, changed: [4]);

        Assert.Contains("def convert(amount, rate):", rendered, StringComparison.Ordinal);
        Assert.Contains("return round(converted, 2)", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("def untouched", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_structure_it_can_read_gets_a_window_and_not_the_file()
    {
        // A thousand-line JSON with one edited value: "the block containing the change" is the whole
        // document, and quoting it would spend the budget saying nothing.
        var lines = new List<DiffLine>();
        for (var i = 1; i <= 1_000; i++)
        {
            lines.Add(new DiffLine(i == 500 ? "+" : " ", i is 1 or 1_000 ? "{" : $"  \"key{i}\": {i},", i, i));
        }

        var rendered = ChangeContext.Render(
            [new FileDiffInfo("data.json", "data.json", "modified", [new DiffHunkInfo("@@", lines)])]);

        Assert.Contains("\"key500\"", rendered, StringComparison.Ordinal);
        Assert.Contains("\"key480\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"key400\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void An_added_file_is_left_to_the_diff_that_already_holds_every_line_of_it()
    {
        var lines = new List<DiffLine> { new("+", "const x = 1;", null, 1) };

        var rendered = ChangeContext.Render(
            [new FileDiffInfo(null, "new.ts", "added", [new DiffHunkInfo("@@", lines)])]);

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void What_the_diff_excludes_is_excluded_here_for_the_same_reasons()
    {
        var lines = new List<DiffLine>
        {
            new(" ", "lockfileVersion: 9", 1, 1),
            new("+", "  new-package: 1.0.0", null, 2),
        };

        var rendered = ChangeContext.Render(
            [new FileDiffInfo("pnpm-lock.yaml", "pnpm-lock.yaml", "modified", [new DiffHunkInfo("@@", lines)])]);

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void A_file_that_cannot_be_given_room_is_named_rather_than_half_shown()
    {
        // A fragment of a declaration reads as the whole of it while showing part — the same reason
        // the diff names a file it cannot fit instead of half-showing it.
        var big = new List<DiffLine> { new(" ", "function big() {", 1, 1) };
        for (var i = 2; i <= 99; i++)
        {
            big.Add(new DiffLine(i == 50 ? "+" : " ", $"  line {i}", i, i));
        }

        big.Add(new DiffLine(" ", "}", 100, 100));

        var small = new List<DiffLine>
        {
            new(" ", "function small() {", 1, 1),
            new("+", "  return 1;", 2, 2),
            new(" ", "}", 3, 3),
        };

        var files = new List<FileDiffInfo>
        {
            new("big.ts", "big.ts", "modified", [new DiffHunkInfo("@@", big)]),
            new("small.ts", "small.ts", "modified", [new DiffHunkInfo("@@", small)]),
        };

        var rendered = ChangeContext.Render(files, budgetChars: 800);

        Assert.Contains("omitted (no room in this prompt): big.ts", rendered, StringComparison.Ordinal);
        // The one that fits still arrives whole: the budget is shared, not spent front to back.
        Assert.Contains("function small() {", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_changes_in_one_declaration_are_quoted_once()
    {
        var rendered = Render("src/Rates.cs", CSharp, changed: [8, 9]);

        var blocks = rendered.Split("── ").Length - 1;
        Assert.Equal(1, blocks);
    }

    /// <summary>The whole file as the diff carries it (<c>GIT-029</c>), with some lines flagged.</summary>
    private static string Render(string path, IReadOnlyList<string> source, IReadOnlyList<int> changed)
    {
        var lines = source
            .Select((text, index) => new DiffLine(changed.Contains(index + 1) ? "+" : " ", text, index + 1, index + 1))
            .ToList();

        return ChangeContext.Render([new FileDiffInfo(path, path, "modified", [new DiffHunkInfo("@@", lines)])]);
    }
}
