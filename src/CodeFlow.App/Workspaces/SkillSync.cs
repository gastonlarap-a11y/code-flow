namespace CodeFlow.Workspaces;

/// <summary>
/// Copies a workspace's enabled skills into a project's own <c>.claude/skills</c>
/// (<c>WS-006</c>).
/// </summary>
/// <remarks>
/// <para>
/// Claude Code only discovers skills relative to its working directory, so a workspace-level store
/// is invisible to it until this runs. Every AI run and every review calls it first.
/// </para>
/// <para>
/// <b>What it manages is decided by name, and by nothing else.</b> There is no marker file and no
/// manifest: a folder under the project's <c>.claude/skills</c> is touched only when its name
/// matches a skill this workspace knows about. A folder the user put there themselves is never
/// read, written or deleted — not on the copy pass, not on the removal pass, not ever. That half of
/// the rule is the one a careless port drops, and it is the difference between syncing and
/// clobbering someone's own work.
/// </para>
/// </remarks>
public static class SkillSync
{
    /// <summary>Runs one sync. Callers treat failure as nothing at all — see the remarks.</summary>
    /// <remarks>
    /// CodeFlow 1.7.2 calls this as <c>let _ = sync_skills_into_project(...)</c> from both AI-run
    /// commands: best-effort, and never a reason to refuse to start a run.
    /// </remarks>
    public static void Run(IReadOnlyList<WorkspaceSkill> skills, string sourceRoot, string projectPath)
    {
        var destinationRoot = Path.Combine(projectPath, ".claude", "skills");

        // Disabled first, and best-effort: a skill switched off is removed from the project on the
        // next sync rather than when it was switched off, which is why toggling one takes effect
        // only at the start of the next run.
        foreach (var skill in skills.Where(s => !s.Enabled))
        {
            TryRemove(Path.Combine(destinationRoot, skill.SkillName));
        }

        var enabled = skills
            .Where(s => s.Enabled)
            .Select(s => s.SkillName)
            .ToHashSet(StringComparer.Ordinal);

        if (enabled.Count == 0 || !Directory.Exists(sourceRoot))
        {
            return;
        }

        Directory.CreateDirectory(destinationRoot);

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot))
        {
            var name = Path.GetFileName(directory);

            if (!enabled.Contains(name))
            {
                continue;
            }

            SkillFiles.CopyDirectory(directory, Path.Combine(destinationRoot, name));
        }
    }

    /// <summary>Runs a sync and swallows whatever it throws.</summary>
    /// <remarks>
    /// The call sites want 1.7.2's <c>let _ = ...</c>, and saying so once here is clearer
    /// than an empty catch block at each of them.
    /// </remarks>
    public static void TryRun(IReadOnlyList<WorkspaceSkill> skills, string sourceRoot, string projectPath)
    {
        try
        {
            Run(skills, sourceRoot, projectPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A project directory that cannot be written is not a reason to refuse the run.
        }
    }

    private static void TryRemove(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best-effort: 1.7.2 discards this error, and the common case is simply that
            // the skill was never synced here in the first place.
        }
    }
}
