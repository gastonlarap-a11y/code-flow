using System.Data.Common;

namespace CodeFlow.Dbml.Introspectors;

/// <summary>Running a catalogue query, once, for the three engines that speak ADO.NET the same way.</summary>
/// <remarks>
/// Typed against <see cref="DbConnection"/> rather than each driver's own class, which is the whole
/// reason the three server introspectors are a query set each and not three copies of the same
/// plumbing. SQLite keeps its own because it reads `PRAGMA` functions, not tables.
/// </remarks>
internal static class Catalogue
{
    /// <summary>How long a catalogue query may take before it is abandoned.</summary>
    /// <remarks>
    /// Generous for a metadata read and finite for a server that accepted the socket and then went
    /// quiet, which is the failure a plain `await` waits out forever.
    /// </remarks>
    public const int TimeoutSeconds = 30;

    public static async Task<List<T>> QueryAsync<T>(
        DbConnection db, string sql, Func<DbDataReader, T> read, CancellationToken cancellationToken)
    {
        var rows = new List<T>();

        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = TimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>A nullable text column, as the empty case rather than a throw.</summary>
    public static string? TextOrNull(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Opens a connection, translating whatever the driver throws into the app's own refusal.
    /// </summary>
    /// <remarks>
    /// <b>The connection string never reaches the message.</b> It holds the password, and a driver
    /// that includes it in an exception — several do — would otherwise put it in a toast, a log and
    /// a bug report. Only the driver's own sentence survives.
    /// </remarks>
    public static async Task OpenOrRefuseAsync(DbConnection db, CancellationToken cancellationToken)
    {
        try
        {
            await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DbException or InvalidOperationException or TimeoutException)
        {
            throw new DbmlConnectionException(e.Message, e);
        }
    }
}
