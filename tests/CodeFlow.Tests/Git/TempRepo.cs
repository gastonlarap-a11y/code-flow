using LibGit2Sharp;

namespace CodeFlow.Tests.Git;

/// <summary>
/// A real git repository in a temporary directory, deleted when the test ends.
/// </summary>
/// <remarks>
/// Every extracted git fixture is <c>kind: "scenario"</c> — 1.7.2's own tests all build a
/// real repository rather than mocking one, because what is being asserted is what libgit2 does to
/// an index and a working tree. Doing anything else here would test this codebase against a guess.
/// </remarks>
internal sealed class TempRepo : IDisposable
{
    private static readonly Signature Author = new("CodeFlow Test", "test@codeflow.local", DateTimeOffset.UnixEpoch);

    public TempRepo()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codeflow-git-{Guid.NewGuid():N}");
        Repository.Init(Path);
        ConfigureIdentity();
    }

    /// <summary>Wraps a directory a caller already produced (e.g. via <c>GitNetwork.CloneAsync</c>)
    /// instead of initializing a new one, so <see cref="Write"/>/<see cref="Commit"/> and disposal
    /// can be reused against it.</summary>
    public TempRepo(string existingPath)
    {
        Path = existingPath;
        ConfigureIdentity();
    }

    // A local identity, so a commit never depends on whoever happens to run the suite, and
    // autocrlf off so checked-out content is byte-identical whatever the host's global config
    // says — both come from 1.7.2's own fixture.
    private void ConfigureIdentity()
    {
        using var repo = Open();
        repo.Config.Set("user.name", Author.Name, ConfigurationLevel.Local);
        repo.Config.Set("user.email", Author.Email, ConfigurationLevel.Local);
        repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
    }

    /// <summary>The working directory.</summary>
    public string Path { get; }

    public Repository Open() => new(Path);

    /// <summary>Writes a file, creating any parent directories.</summary>
    public void Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string relativePath) => File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    public bool Exists(string relativePath) => File.Exists(System.IO.Path.Combine(Path, relativePath));

    public void Delete(string relativePath) => File.Delete(System.IO.Path.Combine(Path, relativePath));

    /// <summary>Adds or replaces a remote, so provider detection has something real to read.</summary>
    public void SetRemote(string name, string url)
    {
        using var repo = Open();

        if (repo.Network.Remotes[name] is not null)
        {
            repo.Network.Remotes.Remove(name);
        }

        repo.Network.Remotes.Add(name, url);
    }

    /// <summary>Stages paths into the real on-disk index.</summary>
    public void Stage(params string[] relativePaths)
    {
        using var repo = Open();
        foreach (var relativePath in relativePaths)
        {
            Commands.Stage(repo, relativePath);
        }
    }

    /// <summary>Stages the given paths and commits them, returning the new commit's id.</summary>
    /// <remarks>
    /// An id rather than a <see cref="LibGit2Sharp.Commit"/> on purpose: the repository handle is
    /// closed when this returns, and a commit's properties are evaluated lazily against that
    /// handle, so reading one afterwards fails with an unrelated-looking libgit2 error.
    /// </remarks>
    public ObjectId Commit(string message, params string[] relativePaths)
    {
        using var repo = Open();
        foreach (var relativePath in relativePaths)
        {
            Commands.Stage(repo, relativePath);
        }

        return repo.Commit(message, Author, Author).Id;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        // Git marks objects read-only; on Windows that blocks a plain recursive delete.
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Path, recursive: true);
    }
}
