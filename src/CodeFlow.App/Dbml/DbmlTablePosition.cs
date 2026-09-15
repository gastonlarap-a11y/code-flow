namespace CodeFlow.Dbml;

/// <summary>Where a person dragged one table of a schema document (DBML-005).</summary>
/// <remarks>
/// The same shape travels both ways — <c>dbml_load_layout</c> returns it and
/// <c>dbml_save_positions</c> receives it — so it is snake_case in both directions, the naming
/// asymmetry <c>ApiCommands</c> documents for rows the renderer sends back.
/// <para>
/// <see cref="TableKey"/> is <c>schema.table</c> in lower case, not a display name: it has to
/// survive a document re-parse, and two tables named <c>orders</c> in different schemas are two
/// positions.
/// </para>
/// </remarks>
public sealed record DbmlTablePosition(string TableKey, double X, double Y);
