using System.Globalization;

namespace CodeFlow.Update;

/// <summary>
/// Comparing the running build's version against a release tag.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than <see cref="Version"/> or a semver package, for two reasons that both
/// bite. <see cref="Version"/> rejects a leading <c>v</c> and anything with a pre-release suffix,
/// which is most of what a GitHub tag looks like; and a string comparison — the obvious shortcut —
/// puts <c>1.7.10</c> below <c>1.7.2</c>, so the app would sit on an old build telling the user it
/// is current.
/// </para>
/// <para>
/// Only what a release tag actually contains is supported: dotted numeric segments and an optional
/// pre-release suffix after a hyphen. Build metadata after <c>+</c> is ignored, as semver says it
/// must be.
/// </para>
/// </remarks>
internal static class ReleaseVersion
{
    /// <summary>Whether <paramref name="candidate"/> is a release worth offering over what runs.</summary>
    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    /// <summary>Negative, zero or positive, the way a comparer reads.</summary>
    public static int Compare(string left, string right)
    {
        var (leftNumbers, leftPre) = Parse(left);
        var (rightNumbers, rightPre) = Parse(right);

        // Missing segments count as zero, so 1.7 and 1.7.0 are the same version rather than the
        // shorter one being smaller.
        for (var i = 0; i < Math.Max(leftNumbers.Count, rightNumbers.Count); i++)
        {
            var a = i < leftNumbers.Count ? leftNumbers[i] : 0;
            var b = i < rightNumbers.Count ? rightNumbers[i] : 0;

            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        // 1.8.0-rc.1 is older than 1.8.0, and offering a pre-release to someone on the release
        // would be a downgrade dressed as an update.
        return (leftPre.Length == 0, rightPre.Length == 0) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => -1,
            _ => string.CompareOrdinal(leftPre, rightPre),
        };
    }

    /// <summary>Splits a tag into its numeric segments and its pre-release suffix.</summary>
    /// <remarks>
    /// A segment that is not a number counts as zero rather than throwing: a tag nobody here chose
    /// the shape of should make the check answer "not newer", not crash the app's update panel.
    /// </remarks>
    private static (IReadOnlyList<int> Numbers, string PreRelease) Parse(string version)
    {
        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var hyphen = text.IndexOf('-', StringComparison.Ordinal);
        var preRelease = hyphen >= 0 ? text[(hyphen + 1)..] : string.Empty;
        var core = hyphen >= 0 ? text[..hyphen] : text;

        var numbers = core
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .ToArray();

        return (numbers, preRelease);
    }
}
