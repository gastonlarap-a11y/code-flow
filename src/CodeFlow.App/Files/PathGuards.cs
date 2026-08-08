namespace CodeFlow.Files;

/// <summary>
/// The two path-traversal guards, which are deliberately not the same guard.
/// See <c>docs/business-rules/11-files-search-terminal.md</c>, <c>FILE-001</c>.
/// </summary>
/// <remarks>
/// <para>
/// One guard is for a path that already exists and one is for a path about to be created, because
/// the check that works for the first is unavailable to the second: containment is decided by
/// canonicalising, and canonicalising a path that is not on disk yet cannot succeed.
/// </para>
/// <para>
/// Both are defensive rather than load-bearing — the app only ever touches files the user picked
/// out of its own tree — which is 1.7.2's stated reason for tolerating the weakness
/// <see cref="ResolveWithinRepo"/> carries.
/// </para>
/// </remarks>
internal static class PathGuards
{
    /// <summary>
    /// Resolves a repo-relative path and rejects anything that escapes the root.
    /// </summary>
    /// <remarks>
    /// When the joined candidate does not exist, canonicalising fails and the fallback is
    /// <see cref="Path.GetFullPath(string)"/> — a lexical normalisation that needs no filesystem —
    /// so <c>..</c> segments are resolved away before the containment check either way. This
    /// closed <c>BUG-FILE-a</c>: 1.7.2 fell back to the raw joined path, and a brand-new file at
    /// <c>../../elsewhere</c> passed the guard. Same shape as the shell's <c>isWithinRoot</c>
    /// (<c>shell/src/app-protocol.ts</c>), which fixed the same defect there (F0.6).
    /// </remarks>
    public static string ResolveWithinRepo(string repoPath, string relPath)
    {
        var root = CanonicalizeRoot(repoPath);
        var candidate = Path.Combine(root, relPath);
        var resolved = TryCanonicalize(candidate) ?? Path.GetFullPath(candidate);

        if (!IsWithin(resolved, root))
        {
            throw new InvalidOperationException("path escapes the repository root");
        }

        return resolved;
    }

    /// <summary>
    /// Resolves the target of a creation, which by definition does not exist yet.
    /// </summary>
    /// <remarks>
    /// Requiring every component to be a plain name is the equivalent guard when there is nothing
    /// to canonicalise, and it also rejects the empty and whitespace-only names the explorer's
    /// inline rename box can produce.
    /// </remarks>
    public static string ResolveNewPath(string repoPath, string relPath)
    {
        var rel = relPath.Trim();
        if (rel.Length == 0)
        {
            throw new InvalidOperationException("name cannot be empty");
        }

        if (Path.IsPathRooted(rel) || !IsPlain(rel))
        {
            throw new InvalidOperationException($"invalid path: {relPath}");
        }

        return Path.Combine(CanonicalizeRoot(repoPath), rel);
    }

    /// <summary>Canonicalises the repository root, which every guard needs first.</summary>
    /// <remarks>
    /// CodeFlow 1.7.2 interpolates the OS error here (<c>invalid repo path: {e}</c>). .NET has no
    /// errno to quote at this point, so the prefix the frontend sees is preserved and the tail is
    /// the path that failed.
    /// </remarks>
    public static string CanonicalizeRoot(string repoPath) =>
        TryCanonicalize(repoPath) ?? throw new InvalidOperationException($"invalid repo path: {repoPath}");

    /// <summary>
    /// Fully resolves a path, or answers <c>null</c> when it does not exist.
    /// </summary>
    /// <remarks>
    /// Every component is resolved, not just the last one: a repository reached through a
    /// symlinked parent has to compare equal to the same repository reached directly, or the
    /// containment checks reject paths that are genuinely inside it. On macOS that is not
    /// hypothetical — <c>/tmp</c> is a link to <c>/private/tmp</c>, and the temporary directories
    /// the tests build live under it.
    /// </remarks>
    private static string? TryCanonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Path.Exists(full))
        {
            return null;
        }

        var root = Path.GetPathRoot(full) ?? string.Empty;
        var resolved = root;

        foreach (var name in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, name);
            resolved = FinalLinkTarget(resolved) ?? resolved;
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    /// <summary>Follows a symlink chain to its end, or answers <c>null</c> if this is not a link.</summary>
    private static string? FinalLinkTarget(string path)
    {
        try
        {
            var target = Directory.Exists(path)
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                : File.ResolveLinkTarget(path, returnFinalTarget: true);

            return target?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A link we cannot follow is left as it is, exactly as canonicalize would leave the
            // path it could not resolve — the containment check then decides.
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the root or lives under it.
    /// </summary>
    /// <remarks>
    /// Containment is decided over path <em>components</em>, never over text: <c>/repo-2/x</c>
    /// starts with <c>/repo</c> as a string and does not as a path. A plain
    /// <see cref="string.StartsWith(string, StringComparison)"/> would let that escape the root,
    /// so a separator after the prefix is required. Do not relax this into a string comparison.
    /// </remarks>
    private static bool IsWithin(string candidate, string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(candidate);

        if (trimmed.Length == root.Length)
        {
            return string.Equals(trimmed, root, StringComparison.Ordinal);
        }

        return trimmed.Length > root.Length
            && trimmed.StartsWith(root, StringComparison.Ordinal)
            && IsSeparator(trimmed[root.Length]);
    }

    /// <summary>
    /// Whether every component is a plain name.
    /// </summary>
    /// <remarks>
    /// A <c>.</c> segment is normalised away unless the path starts with one, so
    /// <c>a/./b</c> is two plain components and <c>./a</c> is not. <c>..</c> is never plain.
    /// Both separators are split on, because
    /// <see cref="Path.AltDirectorySeparatorChar"/> is <c>/</c> on Unix — where a backslash is an
    /// ordinary character in a file name — and <c>/</c> on Windows, where it is a separator.
    /// </remarks>
    private static bool IsPlain(string rel)
    {
        var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            if (part.Length == 0)
            {
                continue;
            }

            if (part == "..")
            {
                return false;
            }

            if (part == "." && i == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSeparator(char c) =>
        c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
