using Microsoft.Data.Sqlite;

namespace CodeFlow.Storage;

/// <summary>
/// Parameter binding and reader plumbing, shared by every feature that touches the database.
/// </summary>
/// <remarks>
/// <para>
/// This is not a repository and deliberately knows nothing about any table: there is no central
/// data-access layer here, so the SQL itself lives with the feature that owns it.
/// What is shared here is the ADO.NET ceremony — creating a command, binding by name, disposing a
/// reader — which every feature would otherwise re-type identically.
/// </para>
/// <para>
/// Null binding is the reason <see cref="Bind"/> exists rather than
/// <c>Parameters.AddWithValue</c> being called directly: a <see langword="null"/> value passed to
/// that method binds nothing at all, and SQLite then rejects the statement for a missing
/// parameter. Projects carry six nullable link columns, so this path is hit constantly.
/// </para>
/// </remarks>
internal static class Sql
{
    /// <summary>Runs a statement and returns how many rows it changed.</summary>
    public static int Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Prepare(connection, sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Reads every row a statement produces.</summary>
    public static List<T> Query<T>(
        SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> read,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Prepare(connection, sql, parameters);
        using var reader = command.ExecuteReader();

        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>Reads the first row a statement produces, or <see langword="null"/> if it produced none.</summary>
    public static T? QuerySingle<T>(
        SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> read,
        params (string Name, object? Value)[] parameters)
        where T : class
    {
        using var command = Prepare(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? read(reader) : null;
    }

    /// <summary>Reads a single text value, distinguishing "no row" from "a row holding an empty string".</summary>
    /// <remarks>
    /// That distinction is load-bearing for <c>app_settings</c>: a stored empty value is a real row
    /// and must come back as <c>""</c>, not as "unset". See <c>WS-004</c>.
    /// </remarks>
    public static string? QueryText(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Prepare(connection, sql, parameters);
        return command.ExecuteScalar() switch
        {
            null or DBNull => null,
            var value => (string)value,
        };
    }

    /// <summary>A column that may be SQL NULL.</summary>
    public static string? TextOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static SqliteCommand Prepare(
        SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
