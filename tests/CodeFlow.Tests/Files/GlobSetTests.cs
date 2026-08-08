using CodeFlow.Files;
using Xunit;

namespace CodeFlow.Tests.Files;

/// <summary>
/// The glob translator that stands in for a glob matcher (<c>FILE-010</c>).
/// </summary>
/// <remarks>
/// <para>
/// No vectors exist for this: in 1.7.2 it is a dependency, not code, so its behaviour was
/// never asserted. What is pinned here is the part of <c>globset</c>'s semantics the search box
/// actually depends on — and specifically the two choices a .NET globbing library would have got
/// wrong, since picking one of those instead was the alternative to writing this.
/// </para>
/// <para>
/// These are private to <see cref="GlobSet"/>'s callers, so they are exercised the way search does:
/// through <see cref="GlobSet.Build"/> and <see cref="GlobSet.IsMatch"/>.
/// </para>
/// </remarks>
public sealed class GlobSetTests
{
    [Fact]
    public void An_empty_list_filters_nothing_at_all()
    {
        // null, not an empty set: "no patterns" has to mean "everything passes", and a set that
        // matched nothing would make include: "" return zero results.
        Assert.Null(GlobSet.Build(""));
        Assert.Null(GlobSet.Build("  ,  ,"));
    }

    /// <summary>A bare pattern is rewritten to <c>**/{pattern}</c>, and <c>**</c> matches zero components.</summary>
    [Theory]
    [InlineData("a.ts", true)]
    [InlineData("src/a.ts", true)]
    [InlineData("src/deep/a.ts", true)]
    [InlineData("src/a.tsx", false)]
    [InlineData("a.md", false)]
    public void A_pattern_with_no_slash_matches_by_file_name_at_any_depth(string path, bool matches) =>
        Assert.Equal(matches, GlobSet.Build("*.ts")!.IsMatch(path));

    /// <summary>
    /// <c>globset</c> leaves <c>literal_separator</c> off, so <c>*</c> crosses directories.
    /// </summary>
    /// <remarks>
    /// Most glob implementations do the opposite, which is the single most likely way a substituted
    /// library would have changed what the search box returns.
    /// </remarks>
    [Theory]
    [InlineData("src/a.ts", true)]
    [InlineData("src/deep/a.ts", true)]
    [InlineData("docs/a.ts", false)]
    public void A_star_matches_a_separator_too(string path, bool matches) =>
        Assert.Equal(matches, GlobSet.Build("src/*")!.IsMatch(path));

    [Theory]
    [InlineData("docs/a.md", true)]
    [InlineData("docs/deep/a.md", true)]
    [InlineData("docs", false)]
    [InlineData("src/a.md", false)]
    public void A_trailing_double_star_matches_everything_below_a_directory(string path, bool matches) =>
        Assert.Equal(matches, GlobSet.Build("docs/**")!.IsMatch(path));

    [Theory]
    [InlineData("src/a.ts", true)]
    [InlineData("src/deep/nest/a.ts", true)]
    [InlineData("a.ts", false)]
    public void A_double_star_in_the_middle_matches_zero_or_more_directories(string path, bool matches) =>
        Assert.Equal(matches, GlobSet.Build("src/**/a.ts")!.IsMatch(path));

    [Fact]
    public void Brace_alternation_is_translated_but_a_comma_inside_it_never_survives_the_list()
    {
        // A group with one alternative works, because nothing splits it.
        var single = GlobSet.Build("**/*.{ts}")!;
        Assert.True(single.IsMatch("src/a.ts"));
        Assert.False(single.IsMatch("src/a.js"));

        // A group with two does not: the list is comma-separated and is split first, so `{ts,js}`
        // reaches the translator as the two fragments `{ts` and `js}`. CodeFlow 1.7.2 splits on the
        // same comma before handing anything to globset, so this is its behaviour too — not a gap
        // in the translation, and not something to "fix" without changing the argument's format.
        var failure = Assert.Throws<InvalidOperationException>(() => GlobSet.Build("**/*.{ts,js}"));
        Assert.StartsWith("invalid glob '**/*.{ts':", failure.Message, StringComparison.Ordinal);

        // Which is why the find box's own way of saying it is a plain list.
        var list = GlobSet.Build("*.ts, *.js")!;
        Assert.True(list.IsMatch("src/a.ts"));
        Assert.True(list.IsMatch("src/a.js"));
    }

    [Theory]
    [InlineData("a1.ts", true)]
    [InlineData("ax.ts", false)]
    public void A_character_class_matches_a_range(string path, bool matches) =>
        Assert.Equal(matches, GlobSet.Build("**/?[0-9].ts")!.IsMatch(path));

    [Theory]
    [InlineData("ax.ts", true)]
    [InlineData("a1.ts", false)]
    public void A_negated_character_class_matches_what_it_excludes(string path, bool matches) =>
        Assert.Equal(matches, GlobSet.Build("**/?[!0-9].ts")!.IsMatch(path));

    [Fact]
    public void A_question_mark_stands_for_exactly_one_character()
    {
        var globs = GlobSet.Build("**/a?.ts")!;

        Assert.True(globs.IsMatch("ab.ts"));
        Assert.False(globs.IsMatch("abc.ts"));
    }

    [Fact]
    public void A_comma_separated_list_matches_if_any_pattern_does()
    {
        var globs = GlobSet.Build("*.ts, docs/**")!;

        Assert.True(globs.IsMatch("src/a.ts"));
        Assert.True(globs.IsMatch("docs/a.md"));
        Assert.False(globs.IsMatch("src/a.md"));
    }

    /// <summary><c>**</c> is only legal as a whole component, exactly as in <c>globset</c>.</summary>
    [Theory]
    [InlineData("src/a**b.ts")]
    [InlineData("src/**b/a.ts")]
    public void A_double_star_that_is_not_a_whole_component_is_rejected(string pattern)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => GlobSet.Build(pattern));

        Assert.StartsWith($"invalid glob '{pattern}':", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unclosed_construct_names_the_pattern_the_user_typed()
    {
        // CodeFlow 1.7.2 interpolates the pattern as typed, not the **/-rewritten one.
        var failure = Assert.Throws<InvalidOperationException>(() => GlobSet.Build("a[b"));

        Assert.StartsWith("invalid glob 'a[b':", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_regex_metacharacter_in_a_pattern_is_a_literal()
    {
        // The translation goes through Regex, so anything it does not itself define has to be
        // escaped on the way — otherwise `a+.ts` would quietly become a repetition.
        var globs = GlobSet.Build("**/a+.ts")!;

        Assert.True(globs.IsMatch("a+.ts"));
        Assert.False(globs.IsMatch("aa.ts"));
    }
}
