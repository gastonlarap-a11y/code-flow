using System.Text.Json.Serialization;

namespace CodeFlow.Git;

/// <summary>Every type this feature puts on the wire.</summary>
/// <remarks>
/// <para>
/// <b>snake_case, not camelCase.</b> The asymmetry is a wire contract, not an oversight:
/// <i>arguments</i> travel camelCase because that is what the renderer sends, while every returned
/// shape and every event payload is snake_case because that is what the renderer reads.
/// <c>renderer/src/types/domain.ts</c> reads <c>current_branch</c>, <c>short_id</c>,
/// <c>parent_ids</c>, <c>new_lineno</c>; <c>commands.ts</c> reads <c>created_at</c> and
/// <c>changed_paths</c>. Changing either side alone breaks the other silently.
/// </para>
/// <para>
/// No <c>DefaultIgnoreCondition</c>. An absent optional must serialise as an explicit
/// <c>null</c>, because the renderer's types declare <c>string | null</c> — a field dropped for
/// being null is a field the UI reads as <c>undefined</c>, which is not the same value.
/// </para>
/// <para>
/// Property names are chosen so the policy produces the expected field: <c>OldLineno</c>, not
/// <c>OldLineNo</c>, which would come out as <c>old_line_no</c>.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RepoStatusInfo))]
[JsonSerializable(typeof(IReadOnlyList<FileDiffInfo>))]
[JsonSerializable(typeof(IReadOnlyList<CommitFileInfo>))]
[JsonSerializable(typeof(IReadOnlyList<CommitInfo>))]
[JsonSerializable(typeof(IReadOnlyList<CheckpointInfo>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<BranchInfo>))]
[JsonSerializable(typeof(IReadOnlyList<StashInfo>))]
[JsonSerializable(typeof(MergeOutcome))]
[JsonSerializable(typeof(IReadOnlyList<ConflictFile>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteInfo>))]
[JsonSerializable(typeof(GitIdentity))]
[JsonSerializable(typeof(GitProgressEvent))]
[JsonSerializable(typeof(GitDoneEvent))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
internal sealed partial class GitJsonContext : JsonSerializerContext;
