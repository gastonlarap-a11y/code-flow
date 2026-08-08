using System.Text.Json.Serialization;

namespace CodeFlow.Update;

/// <summary>Every type this feature puts on the wire, and the one it reads off GitHub.</summary>
/// <remarks>
/// The outbound records carry explicit <c>[JsonPropertyName]</c> attributes rather than relying on
/// a naming policy, because <see cref="ReleasePayload"/> travels the other way and has to match
/// GitHub's names exactly — one policy could not serve both.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpdateAvailability))]
[JsonSerializable(typeof(UpdateInstallation))]
[JsonSerializable(typeof(UpdateProgress))]
[JsonSerializable(typeof(ReleasePayload))]
[JsonSerializable(typeof(string))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
