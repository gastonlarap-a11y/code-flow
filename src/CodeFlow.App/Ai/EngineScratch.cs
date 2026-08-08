using System.Diagnostics;

namespace CodeFlow.Ai;

/// <summary>
/// The scratch files engines hand to their CLIs, and their whole lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// One owner for the naming contract, on purpose: opencode's <c>--file</c> attachment and agy's
/// per-call brief directory are created here, recognised here (so the runner can delete a
/// finished invocation's scratch without widening <c>IAiEngine</c>), and swept here at startup.
/// Splitting those three across files is how <c>BUG-AI-a</c> survived 1.7.2 — the creation sites
/// each assumed someone else would clean up, and nobody did.
/// </para>
/// <para>
/// The startup sweep only removes entries older than <see cref="OrphanAge"/>: the dev shell and
/// an installed CodeFlow can run at the same time, and a young file may be another process's
/// live invocation. An orphan by definition has no process left to touch it, so age is the
/// discriminator that needs no coordination.
/// </para>
/// </remarks>
internal static class EngineScratch
{
    private const string OpenCodePrefix = "codeflow-opencode-";
    private const string AgyPrefix = "codeflow-agy-";

    /// <summary>How old a scratch entry must be before the startup sweep may claim it.</summary>
    internal static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);

    /// <summary>Writes opencode's <c>--file</c> payload. Null on failure — degraded, not fatal.</summary>
    public static string? TryWriteOpenCodePayload(string content)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"{OpenCodePrefix}{Guid.NewGuid()}.txt");
            File.WriteAllText(path, content);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes agy's oversized brief into a per-call directory. Null on failure.</summary>
    /// <remarks>The subdirectory is the unit <c>--add-dir</c> grants, so it scopes agy to exactly
    /// this file — and it is also the unit the cleanup deletes.</remarks>
    public static (string Directory, string File)? TryWriteAgyBrief(string content)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), $"{AgyPrefix}{Guid.NewGuid()}");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "brief.txt");
            File.WriteAllText(file, content);
            return (directory, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The scratch paths a built command references, ready to delete once it has run.</summary>
    /// <remarks>
    /// Recognition by name and location rather than by threading a list through
    /// <see cref="IAiEngine.BuildCommand"/>: the arguments already name every scratch file, the
    /// prefix and temp root are this class's own contract, and the alternative widens an
    /// interface all six engines implement for the benefit of two.
    /// </remarks>
    public static List<string> CollectFrom(ProcessStartInfo startInfo) =>
        startInfo.ArgumentList.Where(IsScratchPath).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Best-effort removal of a finished invocation's scratch.</summary>
    /// <remarks>Swallows filesystem refusals by design: the run's result is already in hand, and
    /// a locked temp file must not turn a successful reply into an error. The startup sweep is
    /// the second chance.</remarks>
    public static void TryDelete(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next startup sweep.
            }
        }
    }

    /// <summary>Deletes scratch entries in <paramref name="tempRoot"/> older than <see cref="OrphanAge"/>.</summary>
    /// <returns>How many entries were removed — surfaced for tests and nothing else.</returns>
    public static int SweepOrphans(string tempRoot, DateTime nowUtc)
    {
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(tempRoot, $"{OpenCodePrefix}*.txt"))
            {
                if (nowUtc - File.GetLastWriteTimeUtc(file) < OrphanAge)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Someone else's problem now; the next sweep retries.
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(tempRoot, $"{AgyPrefix}*"))
            {
                if (nowUtc - Directory.GetLastWriteTimeUtc(directory) < OrphanAge)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Same as above.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // An unreadable temp root must never stop the app from starting.
        }

        return removed;
    }

    private static bool IsScratchPath(string argument)
    {
        if (!argument.StartsWith(Path.GetTempPath(), StringComparison.Ordinal))
        {
            return false;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(argument));
        return name.StartsWith(OpenCodePrefix, StringComparison.Ordinal)
            || name.StartsWith(AgyPrefix, StringComparison.Ordinal);
    }
}
