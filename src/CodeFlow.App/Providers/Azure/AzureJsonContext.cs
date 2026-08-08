using System.Text.Json.Serialization;

namespace CodeFlow.Providers.Azure;

/// <summary>Every shape this client exchanges with Azure DevOps.</summary>
/// <remarks>
/// <para>
/// <b>camelCase, the exact opposite of GitHub's context.</b> Azure's JSON is camelCase throughout —
/// <c>pullRequestId</c>, <c>isDraft</c>, <c>sourceRefName</c>, <c>createdBy</c>, <c>originalObjectId</c>,
/// <c>changeEntries</c>, <c>authenticatedUser</c>, <c>threadContext</c> — so, as with GitHub, the naming
/// policy alone covers every field and the models carry no
/// <see cref="JsonPropertyNameAttribute"/> at all. Two providers, two policies, zero per-field overrides.
/// </para>
/// <para>
/// The outbound provider-neutral types are not here: they belong to <c>ProviderJsonContext</c>, which
/// serialises them snake_case for the renderer. This context is only Azure's own wire vocabulary.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AzureList<AdoProject>))]
[JsonSerializable(typeof(AzureList<AdoRepo>))]
[JsonSerializable(typeof(AzureList<RawPullRequest>))]
[JsonSerializable(typeof(AzureList<RawIteration>))]
[JsonSerializable(typeof(AzureList<RawThread>))]
[JsonSerializable(typeof(RawPullRequest))]
[JsonSerializable(typeof(ChangesResponse))]
[JsonSerializable(typeof(ConnectionData))]
[JsonSerializable(typeof(CreatePullRequestBody))]
[JsonSerializable(typeof(VoteBody))]
[JsonSerializable(typeof(StatusBody))]
[JsonSerializable(typeof(ThreadCreated))]
[JsonSerializable(typeof(ThreadBody))]
[JsonSerializable(typeof(ThreadComment))]
[JsonSerializable(typeof(ThreadStatusBody))]
internal sealed partial class AzureJsonContext : JsonSerializerContext;
