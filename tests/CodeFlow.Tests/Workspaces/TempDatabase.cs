using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// A migrated, throwaway database on disk.
/// </summary>
/// <remarks>
/// A file rather than <c>:memory:</c>, for the same reason the migration tests use one: the schema
/// leans on <c>PRAGMA foreign_keys</c> and on WAL, and a shared in-memory database behaves
/// differently enough that a passing test would prove less than it looks.
/// </remarks>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _path;

    public TempDatabase()
    {
        _path = Path.Combine(Path.GetTempPath(), $"codeflow-ws-{Guid.NewGuid():N}.db");

        // Through Database.Open, not a hand-rolled connection: it is what sets foreign_keys and
        // runs the migrations, and every behaviour under test depends on both.
        Handle = Database.Open(_path);
    }

    public Database Handle { get; }

    /// <summary>
    /// Runs work against the connection, synchronously, for a test's arrange or assert step.
    /// </summary>
    /// <remarks>
    /// Routed through the write path regardless of what the work does, because a test's arrange
    /// step is usually a write whose result it needs. The two paths take the same gate on the same
    /// connection, so the choice costs nothing and one entry point keeps the tests readable.
    /// </remarks>
    public T Use<T>(Func<SqliteConnection, T> work) =>
        Handle.WriteAsync(work, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public void Do(Action<SqliteConnection> work) =>
        Handle.WriteAsync(work, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public void Dispose()
    {
        Handle.DisposeAsync().AsTask().GetAwaiter().GetResult();
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
