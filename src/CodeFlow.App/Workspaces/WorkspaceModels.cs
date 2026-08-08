namespace CodeFlow.Workspaces;

/// <summary>
/// The rows this feature puts on the wire.
/// </summary>
/// <remarks>
/// <para>
/// Field names are the contract. Every JSON key is the literal snake_case name and
/// <c>renderer/src/types/domain.ts</c> reads exactly that — <c>workspace_id</c>, <c>sort_order</c>,
/// <c>created_at</c>. <see cref="WorkspaceJsonContext"/> carries the matching naming policy;
/// renaming a property here silently returns <c>undefined</c> in the renderer rather than failing.
/// </para>
/// <para>
/// Records, because these cross the IPC boundary and nothing mutates them after the reader builds
/// them. See <c>docs/business-rules/03-storage.md</c> §Models for the authoritative field list.
/// </para>
/// <para>
/// <c>GitName</c>/<c>GitEmail</c> are the workspace's commit-identity override (WS-008): both
/// null means "use the global git identity". They are only ever set or cleared as a pair.
/// </para>
/// </remarks>
public sealed record Workspace(
    string Id,
    string Name,
    string Icon,
    string Color,
    long SortOrder,
    string CreatedAt,
    string? GitName,
    string? GitEmail);

/// <summary>A repository registered under a workspace.</summary>
/// <remarks>
/// The six link columns are nullable and independent: nothing in the storage layer stops a project
/// carrying both an ADO and a GitHub link at once, and 1.7.2 does not either.
/// </remarks>
public sealed record Project(
    string Id,
    string WorkspaceId,
    string Name,
    string LocalPath,
    string? RemoteUrl,
    string Color,
    string Icon,
    string? AdoOrg,
    string? AdoProject,
    string? AdoRepoId,
    string? GithubOwner,
    string? GithubRepo,
    string? GithubHost,
    long SortOrder,
    string CreatedAt);

/// <summary>The input side of <c>create_project</c>: a <see cref="Project"/> minus what storage mints.</summary>
/// <remarks>
/// Arrives nested under an <c>input</c> parameter, and — unlike ordinary command arguments, which
/// the renderer sends camelCase — its own keys are snake_case, because it is a whole object rather
/// than a parameter list. The six link fields are optional, so a payload may omit them entirely
/// instead of sending explicit nulls.
/// </remarks>
public sealed record NewProject(
    string WorkspaceId,
    string Name,
    string LocalPath,
    string? RemoteUrl,
    string Color,
    string Icon,
    string? AdoOrg,
    string? AdoProject,
    string? AdoRepoId,
    string? GithubOwner,
    string? GithubRepo,
    string? GithubHost);

/// <summary>A named block of review instructions attached to a workspace.</summary>
/// <remarks>
/// Has no <c>sort_order</c> column, unlike <see cref="WorkspaceAgent"/> — the list order is
/// insertion order and the user cannot rank it.
/// </remarks>
public sealed record ReviewContext(
    string Id,
    string WorkspaceId,
    string Name,
    string Content,
    bool Enabled,
    string CreatedAt);

/// <summary>A user-authored SDD/Harness role with its own AI routing.</summary>
public sealed record WorkspaceAgent(
    string Id,
    string WorkspaceId,
    string Name,
    string Role,
    string Provider,
    string Model,
    string Prompt,
    bool Enabled,
    long SortOrder,
    string CreatedAt);

/// <summary>An MCP server definition scoped to a workspace.</summary>
/// <remarks>
/// <paramref name="Args"/> is a single space-separated string and <paramref name="Env"/> is
/// <c>KEY=value</c> lines, both stored as the user typed them. CodeFlow 1.7.2 keeps them as plain
/// text "so the settings UI can just be a single text input" and re-splits them at launch time;
/// parsing them here would change what a round-trip through the settings screen preserves.
/// </remarks>
public sealed record WorkspaceMcp(
    string Id,
    string WorkspaceId,
    string Name,
    string Command,
    string Args,
    string Env,
    bool Enabled,
    string CreatedAt);

/// <summary>A skill installed into a workspace's own store.</summary>
/// <remarks>
/// <paramref name="SourceRepo"/> is the skills.sh slug it came from, or the literal <c>custom</c>
/// for one authored in-app and <c>local</c> for one imported from a folder. Disabled skills stay
/// installed and stop being synced into projects — that is how 1.7.2 lets someone keep a
/// skill while working with a non-Claude engine.
/// </remarks>
public sealed record WorkspaceSkill(
    string Id,
    string WorkspaceId,
    string SkillName,
    string SourceRepo,
    bool Enabled,
    string InstalledAt);
