using System.Text;
using System.Text.RegularExpressions;

namespace CodeFlow.Files;

/// <summary>
/// The include/exclude glob lists of the search box, standing in for 1.7.2's
/// glob semantics (<c>FILE-010</c>).
/// </summary>
/// <remarks>
/// <para>
/// This translates a glob to a regular expression the way <c>globset</c> itself does, rather than
/// picking a .NET globbing library whose semantics would differ at the edges. Two of its choices
/// are unusual enough to be worth naming, because a library that "looks right" would get both
/// wrong:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>literal_separator</c> is off by default, so <c>*</c> and <c>?</c> <b>do</b> match a
/// <c>/</c>. <c>src/*</c> matches <c>src/a/b.ts</c>, which most glob implementations refuse.
/// </description></item>
/// <item><description>
/// <c>**</c> is only legal as a whole path component — leading, trailing, or between two
/// separators — and matches <em>zero</em> or more of them, which is what makes the
/// <c>**/{pattern}</c> rewrite in <see cref="Build"/> match a file at the repo root as well as one
/// nested inside it.
/// </description></item>
/// </list>
/// <para>
/// The covered syntax is what 1.7.2's own patterns use: <c>*</c>, <c>**</c>, <c>?</c>,
/// character classes with ranges and negation, brace alternation, and backslash escapes. Nested
/// alternation is rejected, as it is in <c>globset</c>.
/// </para>
/// </remarks>
internal sealed class GlobSet
{
    private readonly Regex[] _patterns;

    private GlobSet(Regex[] patterns) => _patterns = patterns;

    /// <summary>
    /// Builds a matcher from a comma-separated glob list, or answers <c>null</c> when the list is
    /// empty — which is 1.7.2's way of saying "this stage filters nothing".
    /// </summary>
    /// <remarks>
    /// A pattern with no <c>/</c> matches by file name anywhere in the tree (<c>*.ts</c>), which is
    /// what people mean and what editors do.
    /// </remarks>
    public static GlobSet? Build(string patterns)
    {
        var list = patterns
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (list.Length == 0)
        {
            return null;
        }

        var compiled = new Regex[list.Length];

        for (var i = 0; i < list.Length; i++)
        {
            var normalized = list[i].Contains('/', StringComparison.Ordinal) ? list[i] : $"**/{list[i]}";

            try
            {
                compiled[i] = new Regex(
                    Translate(normalized),
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                // CodeFlow 1.7.2 names the pattern the user typed, not the rewritten one.
                throw new InvalidOperationException($"invalid glob '{list[i]}': {e.Message}");
            }
        }

        return new GlobSet(compiled);
    }

    /// <summary>Whether any pattern in the set matches this repo-relative path.</summary>
    public bool IsMatch(string path) => _patterns.Any(p => p.IsMatch(path));

    private static string Translate(string glob)
    {
        var pattern = new StringBuilder("^");
        Emit(pattern, glob, 0, glob.Length, insideAlternate: false);
        pattern.Append('$');

        return pattern.ToString();
    }

    private static void Emit(StringBuilder pattern, string glob, int from, int to, bool insideAlternate)
    {
        var i = from;

        while (i < to)
        {
            switch (glob[i])
            {
                case '?':
                    // Not [^/]: with literal_separator off, `?` matches a separator too.
                    pattern.Append('.');
                    i++;
                    break;

                case '*' when i + 1 < to && glob[i + 1] == '*' && !insideAlternate:
                    i = AppendRecursive(pattern, glob, i, to);
                    break;

                case '*':
                    pattern.Append(".*");
                    i++;
                    break;

                case '[':
                    i = AppendClass(pattern, glob, i, to);
                    break;

                case '{' when insideAlternate:
                    throw new FormatException("nested alternate groups are not allowed");

                case '{':
                    i = AppendAlternates(pattern, glob, i, to);
                    break;

                case '\\' when i + 1 < to:
                    pattern.Append(Regex.Escape(glob[i + 1].ToString()));
                    i += 2;
                    break;

                default:
                    pattern.Append(Regex.Escape(glob[i].ToString()));
                    i++;
                    break;
            }
        }
    }

    /// <summary>
    /// Emits one of the three legal <c>**</c> forms and answers the index just past what it consumed.
    /// </summary>
    private static int AppendRecursive(StringBuilder pattern, string glob, int i, int to)
    {
        var after = i + 2;
        var startsComponent = i == 0 || glob[i - 1] == '/';
        var endsComponent = after == to || glob[after] == '/';

        if (!startsComponent || !endsComponent)
        {
            throw new FormatException("invalid use of **; must be one path component");
        }

        if (i == 0)
        {
            // Zero or more leading directories, so `**/x.ts` matches `x.ts` as well as `a/x.ts`.
            pattern.Append("(?:/?|.*/)");
            return after == to ? after : after + 1;
        }

        // Both remaining forms swallow the separator already emitted for glob[i - 1]; nothing else
        // can have produced that trailing '/', because Regex.Escape leaves a slash alone.
        pattern.Length--;

        if (after == to)
        {
            pattern.Append("/.*");
            return after;
        }

        pattern.Append("(?:/|/.*/)");
        return after + 1;
    }

    private static int AppendClass(StringBuilder pattern, string glob, int i, int to)
    {
        var cursor = i + 1;
        var negated = cursor < to && (glob[cursor] == '!' || glob[cursor] == '^');
        if (negated)
        {
            cursor++;
        }

        var body = new StringBuilder();

        // A ']' in first position is a literal, which is how POSIX classes have always worked.
        if (cursor < to && glob[cursor] == ']')
        {
            body.Append("\\]");
            cursor++;
        }

        while (cursor < to && glob[cursor] != ']')
        {
            var c = glob[cursor];

            if (c == '\\' && cursor + 1 < to)
            {
                body.Append(EscapeInClass(glob[cursor + 1]));
                cursor += 2;
                continue;
            }

            // A '-' between two members is a range and stays as it is; anywhere else it is a
            // literal and must not be read as one.
            body.Append(c == '-' && body.Length > 0 && cursor + 1 < to && glob[cursor + 1] != ']'
                ? "-"
                : EscapeInClass(c));

            cursor++;
        }

        if (cursor >= to)
        {
            throw new FormatException("unclosed character class");
        }

        pattern.Append('[').Append(negated ? "^" : string.Empty).Append(body).Append(']');

        return cursor + 1;
    }

    private static int AppendAlternates(StringBuilder pattern, string glob, int i, int to)
    {
        var cursor = i + 1;
        var start = cursor;
        var alternatives = new List<(int Start, int End)>();

        while (cursor < to && glob[cursor] != '}')
        {
            if (glob[cursor] == ',')
            {
                alternatives.Add((start, cursor));
                start = cursor + 1;
            }

            cursor++;
        }

        if (cursor >= to)
        {
            throw new FormatException("unclosed alternate group");
        }

        alternatives.Add((start, cursor));

        pattern.Append("(?:");

        for (var a = 0; a < alternatives.Count; a++)
        {
            if (a > 0)
            {
                pattern.Append('|');
            }

            Emit(pattern, glob, alternatives[a].Start, alternatives[a].End, insideAlternate: true);
        }

        pattern.Append(')');

        return cursor + 1;
    }

    private static string EscapeInClass(char c) =>
        c is '\\' or ']' or '^' or '-' or '[' ? $"\\{c}" : c.ToString();
}
