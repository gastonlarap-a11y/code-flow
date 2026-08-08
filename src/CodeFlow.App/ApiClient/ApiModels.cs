using System.Diagnostics.CodeAnalysis;

namespace CodeFlow.ApiClient;

/// <summary>A request collection: the root of one tree in the API tester's sidebar.</summary>
/// <param name="Auth">JSON <c>AuthConfig</c>, or <c>""</c> when nothing is configured.</param>
/// <param name="Variables">JSON <c>ApiVariable[]</c>.</param>
/// <remarks>
/// <b><c>DIVERGENCE-STORE-a</c> / <c>STORE-023</c>.</b> <see cref="Auth"/> and
/// <see cref="Variables"/> are opaque JSON stored as plain text — including whatever credentials
/// the user typed into a request's auth tab — while the rest of the application keeps secrets in
/// the OS keychain. The asymmetry is 1.7.2's and is reproduced rather than corrected:
/// closing it would mean a schema change and a migration for data that already exists in every
/// 1.7.2 install. It is recorded in the README so nobody discovers it by accident.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification =
        "A request collection in the Postman sense, not a .NET collection: it holds no elements " +
        "and implements no collection interface. The name is 1.7.2's and is what " +
        "renderer/src/types/api.ts declares, so renaming it would rename half of a contract.")]
public sealed record ApiCollection(
    string Id,
    string WorkspaceId,
    string Name,
    string Description,
    string Auth,
    string PreScript,
    string PostScript,
    string Variables,
    long SortOrder,
    string CreatedAt,
    string UpdatedAt);

/// <summary>A folder inside a collection. <paramref name="ParentId"/> null = directly under it.</summary>
public sealed record ApiFolder(
    string Id,
    string CollectionId,
    string? ParentId,
    string Name,
    string Description,
    string Auth,
    string PreScript,
    string PostScript,
    long SortOrder,
    string CreatedAt);

/// <summary>One saved request.</summary>
/// <param name="Method">
/// Denormalised out of <paramref name="Spec"/> so the tree can render a row without opening its
/// blob.
/// </param>
/// <param name="Spec">JSON <c>ApiRequestSpec</c>.</param>
public sealed record ApiRequestRow(
    string Id,
    string CollectionId,
    string? FolderId,
    string Name,
    string Protocol,
    string Method,
    string Url,
    string Spec,
    long SortOrder,
    string CreatedAt,
    string UpdatedAt);

/// <summary>The whole tree in one round trip.</summary>
/// <remarks>
/// The UI rebuilds the nesting client-side from the parent ids, which is cheaper than three chatty
/// queries per expand. Splitting this into separate commands would change how the sidebar loads.
/// </remarks>
public sealed record ApiTree(
    IReadOnlyList<ApiCollection> Collections,
    IReadOnlyList<ApiFolder> Folders,
    IReadOnlyList<ApiRequestRow> Requests);

/// <summary>A named set of variables.</summary>
/// <param name="IsGlobal">
/// The workspace's "Globals" pseudo-environment, seeded by the migration runner. Exactly one row
/// per workspace carries it; it sorts first and cannot be deleted.
/// </param>
public sealed record ApiEnvironment(
    string Id,
    string WorkspaceId,
    string Name,
    string Variables,
    bool IsGlobal,
    long SortOrder,
    string CreatedAt);

/// <summary>One recorded send.</summary>
/// <param name="Snapshot">JSON <c>{ request, response }</c>.</param>
public sealed record ApiHistoryEntry(
    string Id,
    string WorkspaceId,
    string? RequestId,
    string Name,
    string Protocol,
    string Method,
    string Url,
    long? Status,
    long? DurationMs,
    long? SizeBytes,
    string Snapshot,
    string CreatedAt);

/// <summary>One cookie in a workspace's jar.</summary>
/// <remarks>
/// Persisted rather than held in an HTTP client, because the client is rebuilt per request —
/// per-request TLS, proxy and redirect overrides make sharing one impossible.
/// </remarks>
public sealed record ApiCookie(
    string Id,
    string WorkspaceId,
    string Domain,
    string Path,
    string Name,
    string Value,
    bool Secure,
    bool HttpOnly,
    string? Expires,
    string UpdatedAt);
