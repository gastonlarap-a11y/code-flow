using System.Text.Json.Serialization;

namespace CodeFlow.Providers.GitHub;

/// <summary>Every shape this client exchanges with GitHub.</summary>
/// <remarks>
/// <para>
/// <b>snake_case, and it costs nothing here.</b> GitHub's own JSON is already snake_case —
/// <c>created_at</c>, <c>html_url</c>, <c>merged_at</c>, <c>in_reply_to_id</c>, <c>start_line</c> — so
/// the policy alone covers every field and there is exactly one
/// <see cref="JsonPropertyNameAttribute"/> in the models, on <c>ref</c>, because that one collides
/// with a C# keyword.
/// </para>
/// <para>
/// Source-generated rather than walked with <c>JsonDocument</c> like the AI engines. Those target
/// <em>any</em> OpenAI-compatible endpoint and probe two or three leaves defensively; GitHub is a
/// single first-party contract every field of which is mapped, which is what source generation is for.
/// It also keeps this feature consistent with the rest of the app, where the AI engines are the
/// outlier.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RawUser))]
[JsonSerializable(typeof(RawPull))]
[JsonSerializable(typeof(IReadOnlyList<RawPull>))]
[JsonSerializable(typeof(IReadOnlyList<RawPullFile>))]
[JsonSerializable(typeof(IReadOnlyList<RawReview>))]
[JsonSerializable(typeof(IReadOnlyList<RawReviewComment>))]
[JsonSerializable(typeof(IReadOnlyList<RawIssueComment>))]
[JsonSerializable(typeof(CreatePullRequestBody))]
[JsonSerializable(typeof(SubmitReviewBody))]
[JsonSerializable(typeof(CloseBody))]
[JsonSerializable(typeof(CommentCreated))]
[JsonSerializable(typeof(AnchoredCommentBody))]
[JsonSerializable(typeof(CommentBody))]
[JsonSerializable(typeof(GraphQlRequest))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
