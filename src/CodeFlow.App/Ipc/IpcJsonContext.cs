using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Ipc;

/// <summary>
/// The source-generated serializer for everything that crosses the IPC boundary.
/// </summary>
/// <remarks>
/// <para>
/// Source generation is required on hot paths (see <c>.claude/rules/dotnet.md</c>), and every one
/// of the 220 command payloads and 13 event payloads qualifies — each is on the path of every IPC
/// call.
/// </para>
/// <para>
/// The class is <c>partial</c> on purpose: each feature folder declares its own
/// <c>[JsonSerializable]</c> attributes on its own partial declaration, so adding a feature never
/// means editing a shared file that every other feature also touches.
/// </para>
/// <para>
/// <see cref="JsonSourceGenerationOptions.PropertyNamingPolicy"/> is set explicitly and matters:
/// the renderer sends camelCase arguments, and without the policy every DTO would need
/// <c>[JsonPropertyName]</c> on every property — exactly the mapping ceremony this codebase
/// rejects.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(IpcHello))]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(IpcEvent))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(long))]
public partial class IpcJsonContext : JsonSerializerContext;
