using CodeFlow.Dbml.Introspectors;

namespace CodeFlow.Dbml;

/// <summary>Picks the introspector a connection needs (<c>DBML-023</c>).</summary>
/// <remarks>
/// A lookup rather than a DI registration, like <see cref="Ai.EngineCatalog"/>: there is one
/// instance of each and they hold no state, so the composition in <c>Program.cs</c> has nothing to
/// wire. **An unrecognised driver is an error here**, unlike the AI engine catalogue which falls
/// back to Claude — a schema read with the wrong engine is not a degraded answer, it is a wrong one.
/// </remarks>
internal static class DbmlIntrospection
{
    private static readonly IReadOnlyList<IDbmlIntrospector> Engines =
    [
        new PostgresIntrospector(),
        new SqlServerIntrospector(),
        new MySqlIntrospector(),
        new SqliteIntrospector(),
    ];

    /// <exception cref="DbmlConnectionException">No engine reads that driver.</exception>
    public static IDbmlIntrospector For(string driver) =>
        Engines.FirstOrDefault(engine => engine.Driver == driver)
        ?? throw new DbmlConnectionException($"no introspector for driver '{driver}'");
}
