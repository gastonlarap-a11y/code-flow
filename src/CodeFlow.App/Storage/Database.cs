using CodeFlow.Platform;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Storage;

/// <summary>
/// Owns the application's single SQLite connection.
/// </summary>
/// <remarks>
/// <para>
/// CodeFlow 1.7.2 holds one <c>Connection</c> behind a mutex for the life of the process. That is
/// reproduced rather than replaced by pooling: the schema has no version table and relies on
/// startup migrations having finished before anything reads, and a single writer is what the
/// app's access pattern actually is. WAL is enabled so a long read never blocks a write.
/// </para>
/// <para>
/// Access is serialised. <c>Microsoft.Data.Sqlite</c> connections are not thread-safe, and the
/// IPC layer dispatches commands concurrently by design — a slow command must not block the next
/// one, which means two handlers really can reach here at the same time.
/// </para>
/// </remarks>
public sealed class Database : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Database(SqliteConnection connection) => _connection = connection;

    /// <summary>The file this database was opened from.</summary>
    public string Path { get; private init; } = string.Empty;

    /// <summary>
    /// Opens the database and brings its schema fully up to date.
    /// </summary>
    /// <remarks>
    /// Migrations run synchronously and to completion here, before the caller can hand this
    /// object to anything. No command may observe a half-migrated schema — that ordering is the
    /// whole reason the composition root opens storage before it exposes the command surface.
    /// </remarks>
    public static Database Open(string? path = null)
    {
        var file = path ?? AppPaths.DatabaseFile;

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooling off: this is one long-lived connection, and a pool would hold file handles
            // that make the reset-on-next-launch path fail on Windows.
            Pooling = false,
        }.ToString());

        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            // WAL survives across connections once set, so this is a no-op on an existing file.
            // foreign_keys is per-connection and must be set every time — the schema depends on
            // ON DELETE CASCADE for nine tables, and without it a workspace delete orphans them.
            // synchronous is also per-connection: NORMAL is SQLite's recommended pairing with WAL
            // (the FULL default fsyncs per transaction; in WAL that extra durability only matters
            // on power loss, never on an app crash).
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";
            pragma.ExecuteNonQuery();
        }

        Migrations.Run(connection);

        return new Database(connection) { Path = file };
    }

    /// <summary>Runs <paramref name="work"/> against the connection with exclusive access.</summary>
    public async ValueTask<T> ReadAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc cref="ReadAsync{T}"/>
    public async ValueTask WriteAsync(Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A write that returns what it wrote.</summary>
    /// <remarks>
    /// Separate from <see cref="ReadAsync{T}"/> only in name, and worth the duplication: every
    /// create and upsert command has to return the resulting row, and reading those call sites as
    /// "reads" would misdescribe what they do to the next person who opens them.
    /// </remarks>
    public async ValueTask<T> WriteAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
