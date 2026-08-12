using System.Text.Json.Serialization;

namespace CodeFlow.Providers.Azure;

/// <summary>Every shape the work-item client exchanges with Azure Boards.</summary>
/// <remarks>
/// <para>
/// Its own partial context rather than more entries on <c>AzureJsonContext</c>, per the house rule
/// that each feature declares one: work items are a distinct wire vocabulary from pull requests, and
/// the two grow for unrelated reasons.
/// </para>
/// <para>
/// Same camelCase policy, because Azure's JSON is camelCase throughout — <c>workItemRelations</c>,
/// <c>referenceName</c>, <c>errorPolicy</c>. The dotted field keys inside <c>fields</c> escape the
/// policy entirely: they are dictionary <em>keys</em>, not property names, so nothing renames them.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RawWorkItem))]
[JsonSerializable(typeof(AzureList<RawWorkItem>))]
[JsonSerializable(typeof(AzureList<AdoTeam>))]
[JsonSerializable(typeof(AzureList<AdoIteration>))]
[JsonSerializable(typeof(AzureList<AdoWorkItemType>))]
[JsonSerializable(typeof(AzureList<AdoTypeField>))]
[JsonSerializable(typeof(IterationWorkItems))]
[JsonSerializable(typeof(WorkItemComments))]
[JsonSerializable(typeof(RawWorkItemComment))]
[JsonSerializable(typeof(AddCommentBody))]
[JsonSerializable(typeof(WiqlBody))]
[JsonSerializable(typeof(WiqlResult))]
[JsonSerializable(typeof(WorkItemsBatchBody))]
internal sealed partial class AzureWorkItemJsonContext : JsonSerializerContext;
