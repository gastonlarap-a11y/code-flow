namespace CodeFlow.Dbml;

/// <summary>
/// What a real database looks like, read out of it (<c>DBML-025</c>).
/// </summary>
/// <remarks>
/// <b>Structured, not DBML text.</b> The sidecar reports what it found and the renderer writes the
/// document, so DBML emission lives in exactly one place — `lib/dbml/emitDbml.ts`, a pure function
/// a node test can call — instead of once per engine in C# where nothing could test it without a
/// server. It also means a future consumer of the same snapshot does not have to parse DBML back.
/// </remarks>
public sealed record DbmlSchemaSnapshot(
    IReadOnlyList<DbmlSnapshotTable> Tables,
    IReadOnlyList<DbmlSnapshotRef> Refs,
    IReadOnlyList<DbmlSnapshotEnumeration> Enums);

/// <param name="Schema">The namespace the engine files it under; <c>public</c> when it has none.</param>
public sealed record DbmlSnapshotTable(
    string Schema,
    string Name,
    IReadOnlyList<DbmlSnapshotColumn> Columns,
    IReadOnlyList<DbmlSnapshotIndex> Indexes);

/// <param name="Type">As the engine spells it, arguments included — <c>varchar(120)</c>.</param>
/// <param name="Increment">Identity, serial, or <c>AUTO_INCREMENT</c>, depending on the engine.</param>
/// <param name="DefaultValue">
/// The literal or expression the engine reports, already stripped of the casts and quoting each one
/// adds. Null when the column has none.
/// </param>
public sealed record DbmlSnapshotColumn(
    string Name,
    string Type,
    bool Pk,
    bool NotNull,
    bool Unique,
    bool Increment,
    string? DefaultValue);

public sealed record DbmlSnapshotIndex(
    string? Name,
    IReadOnlyList<string> Columns,
    bool Unique,
    bool Pk);

/// <summary>One foreign key, from the side that holds it.</summary>
public sealed record DbmlSnapshotRef(
    string FromSchema,
    string FromTable,
    IReadOnlyList<string> FromColumns,
    string ToSchema,
    string ToTable,
    IReadOnlyList<string> ToColumns,
    string? OnDelete,
    string? OnUpdate);

/// <summary>
/// A user-defined enumerated type. PostgreSQL has them; the other three do not.
/// </summary>
/// <remarks>
/// Not <c>DbmlSnapshotEnum</c>, which reads better and which CA1711 refuses: a type whose name ends
/// in <c>Enum</c> is expected to <em>be</em> an enum, and this is a record describing one.
/// </remarks>
public sealed record DbmlSnapshotEnumeration(string Schema, string Name, IReadOnlyList<string> Values);
