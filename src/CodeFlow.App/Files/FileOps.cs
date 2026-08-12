using System.Diagnostics;
using System.Text;

namespace CodeFlow.Files;

/// <summary>
/// The file operations behind the explorer tree.
/// See <c>docs/business-rules/11-files-search-terminal.md</c>, <c>FILE-001</c>–<c>FILE-006</c>.
/// </summary>
public static class FileOps
{
    /// <summary>UTF-8 that refuses to guess, so an undecodable file is an error and not mojibake.</summary>
    /// <remarks>
    /// Reading must fail on invalid UTF-8, where .NET's default decoder substitutes
    /// U+FFFD. Substituting would turn a Latin-1 file into plausible-looking nonsense in the editor
    /// instead of the refusal 1.7.2 gives, so the strict decoder is the faithful choice.
    /// Content <em>search</em> takes the opposite path on purpose — see <see cref="Search"/>.
    /// </remarks>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>One directory level, directories first, then case-insensitively by name (<c>FILE-002</c>).</summary>
    /// <remarks>
    /// <para>
    /// Whether an entry is a directory comes from the <see cref="FileSystemInfo"/> the enumeration
    /// already produced, not from a second <c>Directory.Exists</c> on the same path. That second
    /// look was a time-of-check race, and it had a symptom: a listing taken while a bulk operation
    /// moved directories around — a checkout, a pull, a branch switch — could answer "not a
    /// directory" for folders that plainly are, and the explorer cached the answer. The tree then
    /// showed the repository's root files with none of its folders until something re-listed
    /// (<c>FILE-007</c>).
    /// </para>
    /// <para>
    /// It is also one <c>stat</c> per entry instead of two, on the path walked every time a
    /// directory is expanded.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<FileEntry> ListDir(string repoPath, string? subPath)
    {
        var target = subPath is null
            ? PathGuards.CanonicalizeRoot(repoPath)
            : PathGuards.ResolveWithinRepo(repoPath, subPath);

        var entries = new List<FileEntry>();

        foreach (var info in new DirectoryInfo(target).EnumerateFileSystemInfos())
        {
            // Skipped by name, independently of gitignore: the explorer never shows git's own
            // storage, and no rule is consulted to decide that.
            if (info.Name == ".git")
            {
                continue;
            }

            var rel = subPath is null ? info.Name : $"{subPath}/{info.Name}";
            var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
            entries.Add(new FileEntry(info.Name, rel.Replace('\\', '/'), isDirectory));
        }

        // OrderBy is a stable sort, which sort_by is too: two entries the comparison calls equal
        // keep the order the filesystem handed them back in.
        return [.. entries.OrderBy(e => e, DirectoriesFirst.Instance)];
    }

    /// <summary>Reads a repo-relative text file.</summary>
    public static string ReadFileText(string repoPath, string relPath)
    {
        var full = PathGuards.ResolveWithinRepo(repoPath, relPath);

        // Checked explicitly so a folder reaching this by mistake says so, instead of surfacing the
        // OS's "Is a directory" as the file's contents.
        if (Directory.Exists(full))
        {
            throw new InvalidOperationException($"{relPath} is a folder, not a file");
        }

        try
        {
            return StrictUtf8.GetString(File.ReadAllBytes(full));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("stream did not contain valid UTF-8");
        }
    }

    /// <summary>Overwrites a repo-relative text file.</summary>
    /// <remarks>
    /// This reuses the existing-path guard even though the target may not exist yet, which is what
    /// makes it the weaker of the two write paths — see <c>BUG-FILE-a</c> on
    /// <see cref="PathGuards.ResolveWithinRepo"/>. Ported as it stands.
    /// </remarks>
    public static void WriteFileText(string repoPath, string relPath, string content) =>
        File.WriteAllText(PathGuards.ResolveWithinRepo(repoPath, relPath), content, StrictUtf8);

    /// <summary>
    /// Writes raw bytes to an <b>absolute</b> path the user chose in a native save dialog
    /// (<c>FILE-005</c>, <c>DIVERGENCE-FILE-d</c>).
    /// </summary>
    /// <remarks>
    /// Deliberately not scoped to a repo like the rest of this type: the whole point of an export
    /// is that it lands wherever the user pointed the dialog. The dialog <em>is</em> the
    /// authorisation, which is why this takes a path rather than a directory-plus-name the caller
    /// could have assembled from something else. Do not add containment here.
    /// </remarks>
    public static void WriteFileBytes(string path, byte[] contents)
    {
        // IsPathFullyQualified, not IsPathRooted: `\foo` is rooted on Windows without naming a
        // drive, and a path with no drive is not absolute enough to write through.
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"expected an absolute path, got: {path}");
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is not null && !Directory.Exists(parent))
        {
            throw new InvalidOperationException($"no such folder: {parent}");
        }

        File.WriteAllBytes(path, contents);
    }

    /// <summary>
    /// Moves a file or directory into <paramref name="destDir"/> (repo-relative; <c>""</c> is the
    /// repo root), keeping its name, and answers the new repo-relative path (<c>FILE-003</c>).
    /// </summary>
    /// <remarks>
    /// This is what the explorer's drag-and-drop calls, so the guards matter more than usual — a
    /// dragged row is a much easier thing to get wrong than a typed command. Both ends resolve
    /// inside the repo; a directory cannot be moved into itself or its own descendant, which the
    /// filesystem would otherwise turn into a lost subtree; and an existing name at the destination
    /// is refused rather than overwritten.
    /// </remarks>
    public static string MovePath(string repoPath, string fromRel, string destDir)
    {
        var source = PathGuards.ResolveWithinRepo(repoPath, fromRel);
        var name = Path.GetFileName(source);
        if (name.Length == 0)
        {
            throw new InvalidOperationException($"cannot move {fromRel}");
        }

        var root = PathGuards.CanonicalizeRoot(repoPath);
        var dest = string.IsNullOrWhiteSpace(destDir)
            ? root
            : PathGuards.ResolveWithinRepo(repoPath, destDir);

        if (!Directory.Exists(dest))
        {
            throw new InvalidOperationException($"{destDir} is not a folder");
        }

        var sourceIsDir = Directory.Exists(source);

        // Comparing canonical paths, so a symlinked route into the subtree is caught too.
        if (sourceIsDir && StartsWith(dest, source))
        {
            throw new InvalidOperationException("cannot move a folder into itself");
        }

        var target = Path.Combine(dest, name);

        if (string.Equals(target, source, StringComparison.Ordinal))
        {
            // Dropped back where it already lives — not an error, just nothing to do.
            return fromRel;
        }

        if (Path.Exists(target))
        {
            throw new InvalidOperationException($"{name} already exists here");
        }

        if (sourceIsDir)
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }

        if (!StartsWith(target, root))
        {
            throw new InvalidOperationException("moved outside the repository");
        }

        return target[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
    }

    /// <summary>
    /// Creates a directory and any missing parents, so <c>a/b/c</c> works in one go — like typing a
    /// nested name into VS Code's explorer.
    /// </summary>
    public static void CreateDir(string repoPath, string relPath)
    {
        var full = PathGuards.ResolveNewPath(repoPath, relPath);

        if (Path.Exists(full))
        {
            throw new InvalidOperationException($"{relPath.Trim()} already exists");
        }

        Directory.CreateDirectory(full);
    }

    /// <summary>Creates an empty file, plus any missing parent directories (<c>FILE-004</c>).</summary>
    /// <remarks>
    /// <see cref="FileMode.CreateNew"/> so an existing file is reported back instead of being
    /// silently truncated.
    /// </remarks>
    public static void CreateFile(string repoPath, string relPath)
    {
        var full = PathGuards.ResolveNewPath(repoPath, relPath);

        var parent = Path.GetDirectoryName(full);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            using var _ = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
        }
        catch (IOException) when (Path.Exists(full))
        {
            throw new InvalidOperationException($"{relPath.Trim()} already exists");
        }
    }

    /// <summary>Opens a repo-relative file with the OS's default application.</summary>
    public static void OpenInDefaultApp(string repoPath, string relPath) =>
        ShellOpen(PathGuards.ResolveWithinRepo(repoPath, relPath));

    /// <summary>
    /// Opens a directory in the OS's file manager — Explorer on Windows, Finder on macOS.
    /// </summary>
    /// <remarks>
    /// Takes an absolute path with no repo scoping at all, exactly as 1.7.2 does: the only
    /// caller hands it a project's own checkout path.
    /// </remarks>
    public static void RevealInFileManager(string path) => ShellOpen(path);

    /// <summary>Opens a directory in VS Code via the <c>code</c> CLI.</summary>
    /// <remarks>
    /// <c>code</c> is a <c>.cmd</c> shim on Windows: spawning it directly rather than through
    /// <c>cmd /C</c> fails to launch (<c>FILE-006</c>).
    /// </remarks>
    public static void OpenInVsCode(string path)
    {
        // Off Windows the name is resolved first: .NET looks a bare name up in this process's PATH,
        // and a Finder-launched macOS app has almost nothing on it. See BinaryDiscovery's
        // XLANG-AI-a note.
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd") { ArgumentList = { "/C", "code", path } }
            : new ProcessStartInfo(Ai.BinaryDiscovery.FindOnPath("code") ?? "code") { ArgumentList = { path } };

        try
        {
            using var _ = Process.Start(start);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"failed to launch VS Code (is `code` on PATH?): {e.Message}");
        }
    }

    /// <summary>Hands a path to the platform's default handler.</summary>
    /// <remarks>
    /// Both callers use the platform's own opener, whose dispatch
    /// is what <see cref="ProcessStartInfo.UseShellExecute"/> already does: <c>ShellExecuteEx</c> on
    /// Windows, <c>/usr/bin/open</c> on macOS, <c>xdg-open</c> on Linux. What is lost is the
    /// Linux fallback chain — it tries <c>gio open</c>, <c>gnome-open</c> and <c>kde-open</c> when
    /// <c>xdg-open</c> is missing, and this does not. On a desktop without <c>xdg-open</c> the two
    /// implementations differ; Linux is outside what this port verifies either way.
    /// </remarks>
    private static void ShellOpen(string path)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(e.Message);
        }
    }

    /// <summary>Component-wise containment, for the two checks that need it after resolution.</summary>
    private static bool StartsWith(string path, string prefix) =>
        path.Length == prefix.Length
            ? string.Equals(path, prefix, StringComparison.Ordinal)
            : path.Length > prefix.Length
                && path.StartsWith(prefix, StringComparison.Ordinal)
                && (path[prefix.Length] == Path.DirectorySeparatorChar
                    || path[prefix.Length] == Path.AltDirectorySeparatorChar);

    /// <summary>Directories before files, then by lowercased name.</summary>
    private sealed class DirectoriesFirst : IComparer<FileEntry>
    {
        public static readonly DirectoriesFirst Instance = new();

        public int Compare(FileEntry? a, FileEntry? b)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            if (a.IsDir != b.IsDir)
            {
                return a.IsDir ? -1 : 1;
            }

            // Ordinal over lowercased names, not a culture-aware comparison: 1.7.2 compares
            // the lowercased strings byte by byte, and a culture would reorder accented names.
            return string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant());
        }
    }
}
