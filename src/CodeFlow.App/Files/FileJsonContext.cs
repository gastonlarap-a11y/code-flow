using System.Text.Json.Serialization;

namespace CodeFlow.Files;

/// <summary>Every type this feature puts on the wire.</summary>
/// <remarks>
/// <para>
/// <b>snake_case out, camelCase in</b>, the same wire asymmetry
/// <see cref="Git.GitJsonContext"/> documents: <c>renderer/src/types/domain.ts</c> reads
/// <c>is_dir</c> and <c>rule_name</c> and <c>commands.ts</c> reads <c>line_no</c> and
/// <c>checkpoint_id</c>, while arguments arrive under their camelCase names.
/// </para>
/// <para>
/// <see cref="SearchOptions"/> is the exception in both directions and carries its own
/// <c>[JsonPropertyName]</c> attributes, because the renderer names that one differently.
/// </para>
/// <para>
/// No <c>DefaultIgnoreCondition</c>: <c>checkpoint_id</c> is <c>string | null</c> on the renderer's
/// side, and a field dropped for being null reads as <c>undefined</c>, which is not the same value.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(IReadOnlyList<FileEntry>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(SearchOutcome))]
[JsonSerializable(typeof(ReplaceOutcome))]
[JsonSerializable(typeof(SearchOptions))]
[JsonSerializable(typeof(string))]
internal sealed partial class FileJsonContext : JsonSerializerContext;
