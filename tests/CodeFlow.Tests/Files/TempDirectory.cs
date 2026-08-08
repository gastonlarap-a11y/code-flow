namespace CodeFlow.Tests.Files;

/// <summary>
/// A scratch directory that cleans itself up.
/// </summary>
/// <remarks>
/// Not <c>TempRepo</c>: <c>fsops</c> has no git dependency at all, and its fixtures say so
/// explicitly ("empty directory, no git init required"). Giving these tests a repository would
/// let one of them come to depend on it without anyone noticing.
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Path = Directory.CreateTempSubdirectory("codeflow-files-").FullName;

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // A test that moved the directory away has nothing left to clean up.
        }
    }
}
