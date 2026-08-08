using System.Globalization;

namespace CodeFlow.Storage;

/// <summary>
/// Timestamps, in the exact textual form 1.7.2 writes.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>created_at</c>, <c>updated_at</c> and <c>installed_at</c> column is <c>TEXT</c>, and
/// rows are ordered by string comparison — <c>ORDER BY sort_order, created_at</c>. So the format
/// is not a display choice, it is a sort key shared with data an existing install already wrote.
/// </para>
/// <para>
/// The stored format is RFC 3339 rendered as
/// <c>2026-07-29T00:31:44.123456789+00:00</c> — a numeric <c>+00:00</c> offset, not <c>Z</c>.
/// .NET's round-trip <c>"o"</c> format would emit <c>Z</c> for a UTC value, which sorts
/// differently from every row already in the file, so the offset is formatted explicitly.
/// </para>
/// </remarks>
internal static class Clock
{
    /// <summary>The current instant, RFC 3339 with an explicit <c>+00:00</c> offset.</summary>
    public static string Now() => Format(DateTimeOffset.UtcNow);

    internal static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture)
            .Replace("Z", "+00:00", StringComparison.Ordinal);
}
