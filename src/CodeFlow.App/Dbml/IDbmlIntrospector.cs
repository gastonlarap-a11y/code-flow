namespace CodeFlow.Dbml;

/// <summary>
/// Reads a schema out of one engine (<c>DBML-023</c>).
/// </summary>
/// <remarks>
/// Four real implementations, which is the bar <c>.claude/rules/dotnet.md</c> sets for an interface.
/// What they share is the shape of the answer and nothing else: every engine names its catalogue
/// differently, and the assembly of their rows into a schema is <see cref="DbmlSnapshotBuilder"/>'s.
/// <para>
/// <b>Read-only, always.</b> Nothing here issues DDL or DML — a schema designer that could write to
/// the database a person pointed it at is a different, much more dangerous tool.
/// </para>
/// </remarks>
internal interface IDbmlIntrospector
{
    /// <summary>The driver id this reads, as <see cref="DbmlDrivers"/> spells it.</summary>
    string Driver { get; }

    /// <summary>Opens a connection and closes it, to prove the details are right.</summary>
    /// <exception cref="DbmlConnectionException">The server refused, or could not be reached.</exception>
    Task ProbeAsync(DbmlConnection connection, string? password, CancellationToken cancellationToken);

    /// <summary>Reads every table, key, relation, index and enum the login can see.</summary>
    /// <exception cref="DbmlConnectionException">The server refused, or could not be reached.</exception>
    Task<DbmlSchemaSnapshot> ReadAsync(
        DbmlConnection connection, string? password, CancellationToken cancellationToken);
}

/// <summary>
/// A database that could not be reached or refused the login.
/// </summary>
/// <remarks>
/// Carries the <c>DB_CONNECTION_REFUSED: </c> sentinel the renderer matches on, composed at the
/// edge like <c>CREDENTIAL_REFUSED:</c> and the rest (<c>XLANG</c>). The message is the driver's own
/// — **never the connection string**, which holds the password.
/// </remarks>
internal sealed class DbmlConnectionException(string message, Exception? inner = null)
    : Exception(Marker + message, inner)
{
    /// <summary>`VERBATIM`. Matched by `renderer/src/lib/dbml/connectionError.ts`.</summary>
    public const string Marker = "DB_CONNECTION_REFUSED: ";
}
