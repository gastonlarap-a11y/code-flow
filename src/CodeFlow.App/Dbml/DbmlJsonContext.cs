using System.Text.Json.Serialization;

namespace CodeFlow.Dbml;

/// <summary>Every type this feature puts on the wire.</summary>
/// <remarks>
/// Its own context rather than an entry in a shared one, per the house rule: the feature owns what
/// it serialises. <b>snake_case out, camelCase in</b> for scalar arguments, the asymmetry
/// <see cref="Files.FileJsonContext"/> documents — except <see cref="DbmlTablePosition"/>, which the
/// renderer sends back as whole objects and therefore keeps its snake_case keys in both directions.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<DbmlTablePosition>))]
[JsonSerializable(typeof(IReadOnlyList<DbmlConnection>))]
[JsonSerializable(typeof(DbmlConnection))]
[JsonSerializable(typeof(NewDbmlConnection))]
[JsonSerializable(typeof(DbmlSchemaSnapshot))]
internal sealed partial class DbmlJsonContext : JsonSerializerContext;
