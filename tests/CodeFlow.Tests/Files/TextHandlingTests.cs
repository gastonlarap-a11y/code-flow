using CodeFlow.Files;
using CodeFlow.Tests.Git;
using Xunit;

namespace CodeFlow.Tests.Files;

/// <summary>
/// The places where .NET's obvious API is not what 1.7.2 does.
/// </summary>
/// <remarks>
/// No vectors cover any of this, because these would be free in a language with the right primitives: <c>str::lines</c>
/// already means what search needs, and <c>chars()</c> already counts what the cap counts. The
/// straightforward .NET translation of each is subtly different, and the difference reaches the
/// user as a wrong line number or a mangled string, so it is asserted here instead.
/// </remarks>
public sealed class TextHandlingTests
{
    /// <summary>
    /// A lone <c>\r</c> is not a line break — <c>EnumerateLines</c> says it is.
    /// </summary>
    /// <remarks>
    /// This is the one that matters most: a hit's line number is what the editor scrolls to, and a
    /// file with an old-Mac line ending or a progress-bar <c>\r</c> in a log would send every hit
    /// after it to the wrong place.
    /// </remarks>
    [Fact]
    public void A_lone_carriage_return_does_not_start_a_new_line()
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", "one\rtwo\nneedle\n");

        var outcome = Search.Find(repo.Path, "needle", new SearchOptions(), 50);

        // Two lines, not three: the needle is on line 2.
        Assert.Equal(2u, Assert.Single(outcome.Hits).LineNo);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\n", 1)]
    [InlineData("a\n\n", 2)]
    [InlineData("a\r\nb", 2)]
    [InlineData("\r\n", 1)]
    [InlineData("a\rb", 1)]
    public void Lines_are_split_the_way_rust_splits_them(string text, int expected) =>
        Assert.Equal(expected, Search.Lines(text).Count());

    [Fact]
    public void A_trailing_newline_does_not_invent_an_empty_last_line()
    {
        Assert.Equal(["a", "b"], Search.Lines("a\nb\n"));
        Assert.Equal(["a", "b"], Search.Lines("a\r\nb\r\n"));
    }

    /// <summary>
    /// The 400-character cap counts Unicode scalar values, not UTF-16 units.
    /// </summary>
    /// <remarks>
    /// Counting <see cref="string.Length"/> would halve the cap for a line of emoji and could cut
    /// between the two halves of a surrogate pair, putting a string that is not valid UTF-16 on the
    /// wire.
    /// </remarks>
    [Fact]
    public void The_line_cap_counts_characters_and_never_splits_a_surrogate_pair()
    {
        // 401 scalar values, each two UTF-16 units: 802 chars by Length, 401 by chars().
        var line = string.Concat(Enumerable.Repeat("😀", 401));

        var truncated = Search.TruncateLine(line);

        Assert.Equal(string.Concat(string.Concat(Enumerable.Repeat("😀", 400)), "…"), truncated);
        Assert.Equal(400, truncated[..^1].EnumerateRunes().Count());

        // No unpaired surrogate survived the cut: EnumerateRunes yields U+FFFD for one, so a cut
        // through a pair would show up here rather than only at the far end of the wire.
        Assert.DoesNotContain(truncated.EnumerateRunes().ToArray(), r => r.Value == 0xFFFD);
    }

    [Fact]
    public void A_line_exactly_at_the_cap_is_left_alone()
    {
        var line = new string('x', 400);

        Assert.Equal(line, Search.TruncateLine(line));
        Assert.Equal($"{line}…", Search.TruncateLine(line + "x"));
    }

    [Fact]
    public void A_lines_trailing_newline_characters_are_stripped_before_it_is_measured() =>
        Assert.Equal("x", Search.TruncateLine("x\r\n"));

    /// <summary>
    /// Escaping a literal query has to make every regex metacharacter inert.
    /// </summary>
    /// <remarks>
    /// .NET's <see cref="System.Text.RegularExpressions.Regex.Escape"/> leaves <c>]</c> and
    /// <c>}</c> alone where a stricter escaper escapes them. Both are literals outside a
    /// group in .NET, so the two agree — but that is a property of the engine rather than of the
    /// escape function, which makes it worth a test rather than a comment.
    /// </remarks>
    [Theory]
    [InlineData("a[b]c")]
    [InlineData("x{2}")]
    [InlineData("a+b")]
    [InlineData("(group)")]
    [InlineData("a|b")]
    [InlineData("^start$")]
    [InlineData("any.char")]
    public void A_literal_query_matches_itself_and_nothing_cleverer(string query)
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", $"{query}\n");
        repo.Write("b.ts", "abc aab x aa xx groupp ab start any_char\n");

        var outcome = Search.Find(repo.Path, query, new SearchOptions(), 50);

        Assert.Equal("a.ts", Assert.Single(outcome.Hits).Path);
    }

    /// <summary>
    /// Search decodes leniently and reading a file for the editor does not.
    /// </summary>
    /// <remarks>
    /// CodeFlow 1.7.2 uses <c>from_utf8_lossy</c> in one and <c>read_to_string</c> in the other, so
    /// a file with one bad byte is still searchable but refuses to open — the asymmetry is real and
    /// both halves are load-bearing, since substituting in the editor would silently corrupt the
    /// file on the next save.
    /// </remarks>
    [Fact]
    public void An_undecodable_file_is_still_searched_but_will_not_open()
    {
        using var repo = new TempRepo();
        File.WriteAllBytes(
            Path.Combine(repo.Path, "latin1.txt"),
            [.. "needle caf"u8.ToArray(), 0xE9, .. "\n"u8.ToArray()]);

        var outcome = Search.Find(repo.Path, "needle", new SearchOptions(), 50);
        Assert.Equal("latin1.txt", Assert.Single(outcome.Hits).Path);

        var failure = Assert.Throws<InvalidOperationException>(
            () => FileOps.ReadFileText(repo.Path, "latin1.txt"));
        Assert.Equal("stream did not contain valid UTF-8", failure.Message);
    }

    [Fact]
    public void A_binary_file_is_not_searched_at_all()
    {
        using var repo = new TempRepo();
        File.WriteAllBytes(Path.Combine(repo.Path, "image.png"), [.. "needle"u8.ToArray(), 0x00, 0x01]);
        repo.Write("a.ts", "needle\n");

        var outcome = Search.Find(repo.Path, "needle", new SearchOptions(), 50);

        // A NUL byte in the first 8 KiB is the same heuristic grep uses.
        Assert.Equal("a.ts", Assert.Single(outcome.Hits).Path);
    }
}
