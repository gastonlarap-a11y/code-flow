using System.Text;
using CodeFlow.Platform;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Tickets;

/// <summary>
/// Where a ticket's mirrored files live on disk.
/// </summary>
/// <remarks>
/// Separate from <see cref="AppPaths"/> because this is policy rather than a constant: the root is
/// the one location in the app a user can redirect, and the layout underneath it is a decision about
/// how someone browsing the folder in Finder should find things — org, then project, then one
/// directory per ticket named so it is readable without opening it.
/// </remarks>
internal static class TicketPaths
{
    /// <summary>The setting that redirects the whole tree. Blank or absent means the default.</summary>
    /// <remarks>
    /// Blank and absent are the same answer here, unlike in <c>app_settings</c> generally
    /// (<c>WS-004</c>): the settings screen clears this field by writing an empty string, and the
    /// only thing a user can mean by clearing it is "go back to the default".
    /// </remarks>
    public const string RootSetting = "tickets_root_dir";

    /// <summary>
    /// Longest a single path segment may get before it is cut.
    /// </summary>
    /// <remarks>
    /// A work item title has no length limit worth relying on, and the whole path has to survive
    /// Windows' 260-character default. Cutting the slug rather than the id keeps the segment
    /// identifiable: the number is what a person searches for.
    /// </remarks>
    private const int MaxSlugLength = 60;

    /// <summary>The configured root, or the default under the app directory.</summary>
    public static string RootFor(SqliteConnection connection) =>
        Settings.GetSetting(connection, RootSetting) is { } configured && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : AppPaths.TicketsRoot;

    /// <summary>One ticket's directory: <c>{root}/{org}/{project}/{id}-{slug}</c>.</summary>
    /// <remarks>
    /// The id leads so the directories sort and complete by the number a person actually quotes, and
    /// so a retitled ticket keeps the same prefix — the slug moving is then a rename of something
    /// recognisable rather than a directory that appears to be new.
    /// </remarks>
    public static string DirectoryFor(string root, string org, string project, string externalId, string title)
    {
        var name = Slug(title) is { Length: > 0 } slug ? $"{Slug(externalId)}-{slug}" : Slug(externalId);
        return Path.Combine(root, Slug(org), Slug(project), name);
    }

    /// <summary>
    /// Reduces arbitrary text to one safe path segment.
    /// </summary>
    /// <remarks>
    /// Deliberately stricter than the filesystem requires — ASCII letters, digits and single
    /// hyphens — rather than filtering the characters each platform rejects. A project named
    /// <c>Payments/Core</c> or a title carrying a colon has to survive on macOS and Windows alike,
    /// and the set that is safe everywhere is small enough to state positively.
    /// </remarks>
    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (Folded(character) is { } folded)
            {
                builder.Append(folded);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }

        return slug;
    }

    /// <summary>An accented Latin letter's ASCII spelling, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <b>An explicit table rather than <c>Normalize(FormD)</c>, and that is not a preference.</b>
    /// <c>Directory.Build.props</c> sets <c>InvariantGlobalization</c>, under which normalisation
    /// does nothing — <c>"Añadir"</c> comes back unchanged and every accent then falls through to
    /// the hyphen branch, giving <c>a-adir</c>. A ticket called <em>Facturación</em> would name its
    /// directory <c>facturaci-n</c>, which is exactly the unreadable result the folding exists to
    /// prevent.
    /// <para>
    /// Both cases are listed instead of lower-casing first, because whether invariant mode maps
    /// non-ASCII case at all is a platform detail this should not depend on. The table covers the
    /// Latin-1 range the app's own languages use; anything outside it still degrades to a hyphen,
    /// which is correct for scripts that have no ASCII spelling.
    /// </para>
    /// </remarks>
    private static string? Folded(char character) => character switch
    {
        'á' or 'à' or 'â' or 'ä' or 'ã' or 'å' or 'Á' or 'À' or 'Â' or 'Ä' or 'Ã' or 'Å' => "a",
        'é' or 'è' or 'ê' or 'ë' or 'É' or 'È' or 'Ê' or 'Ë' => "e",
        'í' or 'ì' or 'î' or 'ï' or 'Í' or 'Ì' or 'Î' or 'Ï' => "i",
        'ó' or 'ò' or 'ô' or 'ö' or 'õ' or 'ø' or 'Ó' or 'Ò' or 'Ô' or 'Ö' or 'Õ' or 'Ø' => "o",
        'ú' or 'ù' or 'û' or 'ü' or 'Ú' or 'Ù' or 'Û' or 'Ü' => "u",
        'ñ' or 'Ñ' => "n",
        'ç' or 'Ç' => "c",
        'ý' or 'ÿ' or 'Ý' => "y",
        'æ' or 'Æ' => "ae",
        'œ' or 'Œ' => "oe",
        'ß' => "ss",
        _ => null,
    };
}
