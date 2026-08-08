using LibGit2Sharp;

namespace CodeFlow.Providers.Azure;

/// <summary>
/// Renders one file's two versions as a git-style unified diff.
/// </summary>
/// <remarks>
/// <para>
/// Azure DevOps has no endpoint that returns a pull request's diff as text, so the diff is assembled
/// here from the two blobs of every changed file. That is what makes "review a pull request from a
/// pasted link" possible with no clone on disk.
/// </para>
/// <para>
/// A blob-to-blob patch that can name a path is what this needs, and LibGit2Sharp does not wrap one.
/// LibGit2Sharp does not wrap <c>git_patch_from_buffers</c>, and its blob-to-blob overloads —
/// <c>Diff.Compare(Blob, Blob)</c> and <c>Diff.Compare(Blob, Blob, CompareOptions)</c> — take no path,
/// so libgit2 would label the header with its own placeholder instead of <c>a/src/app.ts</c>. Naming
/// the path requires trees, so this writes both sides into a throwaway bare repository and compares
/// tree to tree. Same libgit2, same renderer, same output; only the entry point differs.
/// </para>
/// </remarks>
internal static class UnifiedPatch
{
    /// <summary>Renders <paramref name="path"/> changing from <paramref name="before"/> to <paramref name="after"/>.</summary>
    /// <returns>
    /// The patch text — empty when the two sides are identical — or <c>null</c> when libgit2 cannot produce
    /// one at all, which the caller renders as binary. A genuinely binary file is <em>not</em> that case:
    /// libgit2 renders it as its own "Binary files … differ" line, so the caller's binary placeholder is
    /// rarer than its name suggests. That is true of 1.7.2 too, whose <c>None</c> means only that
    /// the rendered buffer was absent or not valid UTF-8.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Both trees always carry an entry for the path.</b> A side that does not exist is an entry
    /// pointing at an <em>empty</em> blob, never an omitted path — and getting this wrong is the one way
    /// this approach can diverge from what the UI expects. An empty buffer is still a present side, so
    /// the diff is handed two sides on every call and can never produce an <c>Added</c> or
    /// <c>Deleted</c> delta: an added file renders as a modification of an empty file. Omitting the path
    /// from the old tree would let libgit2 detect the addition from tree membership and emit
    /// <c>new file mode 100644</c> and <c>--- /dev/null</c>, which the renderer never expects.
    /// </para>
    /// <para>
    /// The mode is fixed on both sides for the same reason: Azure's change list carries no executable
    /// bit, and <c>from_buffers</c> carries no mode at all, so neither renders a permission change.
    /// </para>
    /// <para>
    /// One repository per call rather than one per pull request, because <see cref="Repository"/> is not
    /// thread-safe and up to six files render concurrently. Initialising a bare repository is a few small
    /// writes against a diff whose cost is dominated by two blob downloads per file.
    /// </para>
    /// </remarks>
    public static string? Render(string path, byte[] before, byte[] after)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"codeflow-ado-diff-{Guid.NewGuid():n}");

        try
        {
            Repository.Init(directory, isBare: true);
            using var repository = new Repository(directory);

            // Disposed, and that is what actually lets the directory go on Windows. `Patch` owns
            // native diff state; without this the handles outlive `repository`'s own Dispose and the
            // removal below fails however many times it retries — which is exactly what CI showed.
            using var patch = repository.Diff.Compare<Patch>(
                Snapshot(repository, path, before), Snapshot(repository, path, after));

            // Empty is a result, not a failure. Two identical sides render to no text at all, and the
            // reference returns that empty string rather than None — its caller appends it, contributing
            // nothing, and a pull request whose every file rendered empty is what raises "no file changes
            // to review". Returning null here instead would label an unchanged file as binary.
            return patch.Content;
        }
        catch (LibGit2SharpException)
        {
            return null;
        }
        finally
        {
            Discard(directory);
        }
    }

    /// <summary>One side of the comparison: a tree holding exactly this path.</summary>
    private static Tree Snapshot(Repository repository, string path, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        var blob = repository.ObjectDatabase.CreateBlob(stream);

        return repository.ObjectDatabase.CreateTree(
            new TreeDefinition().Add(path, blob, Mode.NonExecutableFile));
    }

    /// <summary>
    /// Removes the throwaway repository, and does not fail the diff if it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A leaked temporary directory is a housekeeping problem; turning it into an exception would lose a
    /// pull-request diff that has already been fetched and rendered.
    /// </para>
    /// <para>
    /// <b>It retries, because on Windows the first attempt loses a race.</b> Unix unlinks a file whose
    /// handle is still open; Windows refuses, and libgit2 has not always released the pack and index
    /// handles by the time <c>Repository.Dispose</c> returns. The first CI run on Windows caught this —
    /// every rendered Azure diff left a directory behind, silently, because the <c>IOException</c> was
    /// swallowed here. Three quick attempts cover the gap; giving up after them keeps the original
    /// promise that a diff is never lost over housekeeping.
    /// </para>
    /// </remarks>
    private static void Discard(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (TryDiscard(directory))
            {
                return;
            }

            // Short enough not to be felt on a path that already made a network call, long enough for a
            // handle to be released.
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Takes the read-only attribute off every file, so Windows will delete them.
    /// </summary>
    /// <remarks>
    /// <b>This is what the leak actually was.</b> Git writes loose objects read-only — they are
    /// content-addressed and meant never to change — and on Windows the read-only <em>attribute</em>
    /// makes <c>File.Delete</c> throw <c>UnauthorizedAccessException</c>, which
    /// <c>Directory.Delete(recursive: true)</c> surfaces and this method used to swallow. Unix does
    /// not care: permission to unlink comes from the containing directory, not the file. So every
    /// rendered Azure diff leaked a directory there and none here, and CI counted 197 of them
    /// accumulated across a single run before the test that checks for one even got to run.
    /// </remarks>
    private static void ClearReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    /// <summary>One removal attempt. False when something still holds the directory.</summary>
    private static bool TryDiscard(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                ClearReadOnly(directory);
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
