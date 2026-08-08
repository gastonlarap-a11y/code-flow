using System.Text.RegularExpressions;
using CodeFlow.Files;
using Xunit;

namespace CodeFlow.Tests.Files;

/// <summary>
/// The other half of the anchor pattern's cross-language contract.
/// </summary>
/// <remarks>
/// <para>
/// <c>renderer/src/lib/anchors.ts</c> builds a regex source string and sends it to
/// <c>search_repo</c>, which compiles it here. Its own comment warns that if the two engines
/// disagree the panel "would list files the second pass then finds nothing in" — an empty panel and
/// no error. Nothing checked that, on either side, until now.
/// </para>
/// <para>
/// The literal below is pinned identically by <c>anchors.test.ts</c>. Neither test proves the other
/// side compiles it — they cannot run each other's runtime — but changing either half without the
/// other fails one of them, which is what turns a silent break into a loud one.
/// </para>
/// <para>
/// The real constraint is <see cref="RegexOptions.NonBacktracking"/>, which
/// <see cref="Search"/> uses and which rejects lookaround and backreferences outright. A pattern
/// that worked in the editor's JavaScript engine and used either would throw here at search time.
/// </para>
/// </remarks>
public sealed class AnchorPatternTests
{
    /// <summary>Byte-for-byte what <c>anchorPatternSource(["TODO", "FIXME"])</c> returns.</summary>
    // Five-quote delimiters, because the pattern itself contains a Python docstring opener.
    private const string Pattern =
        """""(?://+|/\*+|\*+|#+|--+|<!--+|;+|%+|"""|''')\s*(TODO|FIXME)\b[:\-]?[ \t]*""""";

    [Fact]
    public void The_pattern_compiles_under_the_options_search_actually_uses()
    {
        // NonBacktracking is the part that matters: it is a smaller language than .NET's default
        // engine, so "it compiles" is not the same question as "it compiles here".
        var matcher = new Regex(Pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

        Assert.Matches(matcher, "// TODO: extraer esto");
    }

    [Theory]
    [InlineData("// TODO: c-family")]
    [InlineData("# FIXME: shell")]
    [InlineData("-- TODO: sql")]
    [InlineData("<!-- FIXME: html -->")]
    [InlineData("; TODO: ini")]
    [InlineData("% FIXME: latex")]
    [InlineData("\"\"\" TODO: docstring")]
    [InlineData(" * TODO: block continuation")]
    [InlineData("// TODO")]
    public void Every_comment_opener_the_editor_recognises_matches_here_too(string line) =>
        Assert.Matches(Compiled(), line);

    [Theory]
    [InlineData("// TODOS: plural")]
    [InlineData("const x = 1;")]
    [InlineData("// just a comment")]
    public void What_the_editor_refuses_is_refused_here_too(string line) =>
        Assert.DoesNotMatch(Compiled(), line);

    [Fact]
    public void The_tag_comes_back_in_the_capture_group_the_frontend_reads()
    {
        // The hits from `search_repo` are re-parsed on the frontend, which reads group 1 as the tag.
        // A regrouped pattern would return files with no recognisable anchors in them.
        var match = Compiled().Match("  # FIXME: revisar");

        Assert.Equal("FIXME", match.Groups[1].Value);
    }

    private static Regex Compiled() =>
        new(Pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}
