namespace CodeFlow.Dbml;

/// <summary>
/// Finding the <c>.dbml</c> files in a project folder (<c>DBML-001</c>).
/// </summary>
/// <remarks>
/// <para>
/// A walk of its own rather than <see cref="Files.RepoWalk"/>, which is the obvious thing to reach
/// for and does not fit: that one takes an open <see cref="LibGit2Sharp.Repository"/> because it
/// prunes through <c>Repository.Ignore.IsPathIgnored</c>. A schema designer has to work in a plain
/// folder (<c>GIT-039</c>), so there is no repository to hand it.
/// </para>
/// <para>
/// It prunes rather than filters, for the reason <c>DIVERGENCE-FILE-a</c> gives: a directory in
/// <see cref="PrunedDirectories"/> is never read from disk. The list is a fixed set of build and
/// dependency directories instead of gitignore rules — without git there is nothing to ask, and a
/// <c>.dbml</c> under <c>node_modules</c> belongs to a dependency, not to the user.
/// </para>
/// </remarks>
internal static class DbmlDocuments
{
    /// <summary>The extension that makes a file a schema document. Compared case-insensitively.</summary>
    private const string Extension = ".dbml";

    /// <summary>
    /// Ceiling on how many documents are returned. A schema designer lists these in a picker, and
    /// nobody navigates past a few dozen; the cap exists so a pathological tree cannot stall the UI.
    /// </summary>
    public const int MaxDocuments = 2_000;

    /// <summary>
    /// How deep the walk goes. Deliberately finite: a symlink loop is otherwise unbounded, and this
    /// walk has no repository to tell it where the project ends.
    /// </summary>
    private const int MaxDepth = 24;

    /// <summary>
    /// Directories never descended into. Build output and vendored dependencies, whose schemas
    /// belong to something else.
    /// </summary>
    private static readonly HashSet<string> PrunedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "out", "target",
        ".venv", "venv", "__pycache__", "vendor", ".next", ".nuxt", ".svelte-kit",
        ".gradle", ".idea", ".vs", "Pods", "DerivedData",
    };

    /// <summary>
    /// Every <c>.dbml</c> file under <paramref name="rootPath"/>, project-relative, sorted, with
    /// forward slashes on every platform.
    /// </summary>
    /// <remarks>
    /// Separators are normalised because the path is half of the layout key
    /// (<c>project_id, rel_path, table_key</c>): a document found as <c>db\schema.dbml</c> on
    /// Windows and <c>db/schema.dbml</c> on macOS has to be the same row, or the same file in the
    /// same project answers to two layouts.
    /// </remarks>
    public static IReadOnlyList<string> List(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"no such folder: {rootPath}");

        var found = new List<string>();
        Walk(rootPath, string.Empty, 0, found);
        found.Sort(StringComparer.OrdinalIgnoreCase);

        return found;
    }

    private static void Walk(string directory, string relativePrefix, int depth, List<string> found)
    {
        if (depth > MaxDepth || found.Count >= MaxDocuments) return;

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // One unreadable directory is not a failed listing: a project can legitimately contain
            // a folder this process cannot open, and refusing the whole walk over it would make the
            // picker empty for a reason the user cannot see or fix.
            return;
        }

        foreach (var entry in entries)
        {
            if (found.Count >= MaxDocuments) return;

            var name = Path.GetFileName(entry);
            var relative = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";

            if (Directory.Exists(entry))
            {
                // Symlinked directories are skipped rather than followed: `MaxDepth` already bounds
                // a loop, but a link pointing back up the tree would still report the same file
                // under several paths, and each of those is a distinct layout key.
                var info = new DirectoryInfo(entry);
                if (info.LinkTarget is not null) continue;
                if (PrunedDirectories.Contains(name)) continue;

                Walk(entry, relative, depth + 1, found);
                continue;
            }

            if (name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) found.Add(relative);
        }
    }
}
