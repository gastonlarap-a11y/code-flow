using System.Text;
using CodeFlow.Platform;

namespace CodeFlow.Workspaces;

/// <summary>
/// A skill's folder on disk: where it lives, what may be read or written inside it, and the two
/// creation paths that do not go through <c>npx</c>.
/// See <c>docs/business-rules/09-workspace-scoped.md</c>, <c>WS-007</c>.
/// </summary>
public static class SkillFiles
{
    /// <summary>Where a workspace's skills live, one directory per skill.</summary>
    /// <remarks>
    /// <para>
    /// The <c>.claude/skills</c> tail is not decoration: Claude Code only discovers skills at that
    /// path relative to a working directory, which is also why <see cref="SkillSync"/> exists.
    /// </para>
    /// <para>
    /// This is the only function here that reads <see cref="AppPaths"/>. Everything else takes the
    /// root it works under, so the tests can exercise the real code against a temporary directory
    /// instead of writing into the user's own <c>~/CodeFlow</c>.
    /// </para>
    /// </remarks>
    public static string RootFor(string workspaceId) =>
        Path.Combine(AppPaths.WorkspaceSkillsDirectory(workspaceId), ".claude", "skills");

    public static string Directory(string skillsRoot, string name) => Path.Combine(skillsRoot, name);

    /// <summary>Every file inside a skill, skill-relative and <c>/</c>-separated, sorted.</summary>
    /// <remarks>
    /// A skill that does not exist lists nothing rather than failing — 1.7.2's
    /// <c>collect_files</c> returns early on a missing directory.
    /// </remarks>
    public static IReadOnlyList<string> ListFiles(string skillsRoot, string skillName)
    {
        var root = Directory(skillsRoot, skillName);
        var files = new List<string>();

        Collect(root, root, files);
        files.Sort(StringComparer.Ordinal);

        return files;
    }

    public static string ReadFile(string skillsRoot, string skillName, string relPath) =>
        File.ReadAllText(SafePath(skillsRoot, skillName, relPath));

    public static void WriteFile(string skillsRoot, string skillName, string relPath, string content)
    {
        var path = SafePath(skillsRoot, skillName, relPath);

        var parent = Path.GetDirectoryName(path);
        if (parent is not null)
        {
            System.IO.Directory.CreateDirectory(parent);
        }

        File.WriteAllText(path, content);
    }

    public static void DeleteFile(string skillsRoot, string skillName, string relPath) =>
        File.Delete(SafePath(skillsRoot, skillName, relPath));

    /// <summary>Creates a skill from a <c>SKILL.md</c> the user authored in-app.</summary>
    public static string CreateCustom(string skillsRoot, string name, string skillMd)
    {
        var clean = SanitizeName(name);
        if (clean.Length == 0)
        {
            throw new InvalidOperationException("Please give the skill a name");
        }

        var directory = Directory(skillsRoot, clean);
        if (System.IO.Directory.Exists(directory))
        {
            throw new InvalidOperationException($"A skill named \"{clean}\" already exists in this workspace");
        }

        System.IO.Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), skillMd);

        return clean;
    }

    /// <summary>Copies a skill in from a local folder. Its own folder name becomes the skill name.</summary>
    public static string ImportFromFolder(string skillsRoot, string sourceDirectory)
    {
        if (!File.Exists(Path.Combine(sourceDirectory, "SKILL.md")))
        {
            throw new InvalidOperationException("That folder isn't a skill — it has no SKILL.md");
        }

        var name = SanitizeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDirectory)));
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Couldn't derive a skill name from that folder");
        }

        var directory = Directory(skillsRoot, name);
        if (System.IO.Directory.Exists(directory))
        {
            throw new InvalidOperationException($"A skill named \"{name}\" already exists in this workspace");
        }

        CopyDirectory(sourceDirectory, directory);

        return name;
    }

    /// <summary>Removes a skill's folder, or says why it could not.</summary>
    /// <remarks>
    /// The failure propagates — this closed <c>BUG-WS-a</c>: the old best-effort variant
    /// swallowed it, and a folder that could not be deleted (open in an editor, locked on
    /// Windows, permissions) was orphaned with no row left to find it by, permanently blocking
    /// its name. A folder that is already gone is fine: the goal state is reached.
    /// </remarks>
    public static void RemoveDirectory(string skillsRoot, string skillName)
    {
        var directory = Directory(skillsRoot, skillName);
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not delete the skill's folder — close anything using it and try again: {e.Message}");
        }
    }

    /// <summary>Copies a directory tree, creating the destination.</summary>
    internal static void CopyDirectory(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);

        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));

            if (System.IO.Directory.Exists(entry))
            {
                CopyDirectory(entry, target);
            }
            else
            {
                File.Copy(entry, target, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Joins a skill-relative path, rejecting anything that would leave the skill's folder
    /// (<c>WS-007</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a third traversal guard, and deliberately not either of the two in
    /// <c>Files/PathGuards.cs</c>. It rejects <c>..</c> and empty segments and then stops — there is
    /// no canonicalisation and no containment check afterwards, so it is purely syntactic. Reusing
    /// the file-explorer guards here would be a stricter app than 1.7.2 is.
    /// </para>
    /// <para>
    /// Both separators are checked whatever the platform, because 1.7.2 splits on both.
    /// </para>
    /// </remarks>
    internal static string SafePath(string skillsRoot, string skillName, string relPath)
    {
        if (relPath.Split('/', '\\').Any(segment => segment == ".." || segment.Length == 0))
        {
            throw new InvalidOperationException("invalid file path");
        }

        return Path.Combine(Directory(skillsRoot, SanitizeName(skillName)), relPath);
    }

    /// <summary>Keeps a skill name usable as a single path segment.</summary>
    /// <remarks>
    /// Anything that is not a letter, digit, dash, underscore or dot becomes a dash — including
    /// separators, which is what stops a name from becoming a path — and dashes, dots and spaces are
    /// then trimmed from both ends.
    /// </remarks>
    internal static string SanitizeName(string name)
    {
        var mapped = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            mapped.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }

        return mapped.ToString().Trim('-', '.', ' ');
    }

    private static void Collect(string root, string directory, List<string> output)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
        {
            if (System.IO.Directory.Exists(entry))
            {
                Collect(root, entry, output);
            }
            else
            {
                output.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        }
    }
}
