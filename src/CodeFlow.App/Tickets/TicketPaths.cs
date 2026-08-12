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
    /// Where a ticket's mirror goes: where it already is, or a fresh name from its title.
    /// </summary>
    /// <remarks>
    /// <b>A mirror never moves.</b> The directory used to be recomputed from the current title on
    /// every sync, so renaming the work item on the board silently relocated it — and left the
    /// user's <c>notes/</c>, the one thing in there nobody else owns, stranded in a directory the
    /// app would never open again. Nothing moved them and nothing said so. <c>WI-003</c> promises
    /// that directory survives a resync; this is what makes the promise hold across a rename, and it
    /// is a named function rather than a condition inside the sync so it can be stated once and
    /// tested without a network or a keychain.
    /// </remarks>
    /// <param name="existing">
    /// The <c>mirror_path</c> already cached for this ticket, or <see langword="null"/> the first
    /// time it is seen. Blank counts as absent — a row written before this column meant anything.
    /// </param>
    public static string MirrorFor(
        string? existing, string root, string org, string project, string externalId, string title) =>
        string.IsNullOrWhiteSpace(existing)
            ? DirectoryFor(root, org, project, externalId, title)
            : existing;

    /// <summary>
    /// Reduces arbitrary text to one safe path segment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately stricter than the filesystem requires — ASCII letters, digits and single
    /// hyphens — rather than filtering the characters each platform rejects. A project named
    /// <c>Payments/Core</c> or a title carrying a colon has to survive on macOS and Windows alike,
    /// and the set that is safe everywhere is small enough to state positively.
    /// </para>
    /// <para>
    /// <b>Case is preserved.</b> It used to be lower-cased, and a user who opened the folder for
    /// <em>CF-E2E Ajuste de tabla (criterios en prosa)</em> found
    /// <c>3-cf-e2e-ajuste-de-tabla-criterios-en-prosa</c> and reported it as wrong. It was not
    /// wrong, but it was unreadable, and nothing was gained by it: the characters that make a path
    /// awkward are spaces and punctuation, not capitals. Both are still gone.
    /// </para>
    /// </remarks>
    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
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
    /// Both cases are listed — and now spell their ASCII replacement in the matching case — because
    /// whether invariant mode maps non-ASCII case at all is a platform detail this should not depend
    /// on. Calling <c>ToUpperInvariant</c> on the folded result would be the same bet in a different
    /// place. The table covers the Latin-1 range the app's own languages use; anything outside it
    /// still degrades to a hyphen, which is correct for scripts that have no ASCII spelling.
    /// </para>
    /// </remarks>
    private static string? Folded(char character) => character switch
    {
        'á' or 'à' or 'â' or 'ä' or 'ã' or 'å' => "a",
        'Á' or 'À' or 'Â' or 'Ä' or 'Ã' or 'Å' => "A",
        'é' or 'è' or 'ê' or 'ë' => "e",
        'É' or 'È' or 'Ê' or 'Ë' => "E",
        'í' or 'ì' or 'î' or 'ï' => "i",
        'Í' or 'Ì' or 'Î' or 'Ï' => "I",
        'ó' or 'ò' or 'ô' or 'ö' or 'õ' or 'ø' => "o",
        'Ó' or 'Ò' or 'Ô' or 'Ö' or 'Õ' or 'Ø' => "O",
        'ú' or 'ù' or 'û' or 'ü' => "u",
        'Ú' or 'Ù' or 'Û' or 'Ü' => "U",
        'ñ' => "n",
        'Ñ' => "N",
        'ç' => "c",
        'Ç' => "C",
        'ý' or 'ÿ' => "y",
        'Ý' => "Y",
        'æ' => "ae",
        // "Ae", not "AE": it sits inside a word, where an all-caps pair reads as an acronym.
        'Æ' => "Ae",
        'œ' => "oe",
        'Œ' => "Oe",
        'ß' => "ss",
        _ => null,
    };
}
