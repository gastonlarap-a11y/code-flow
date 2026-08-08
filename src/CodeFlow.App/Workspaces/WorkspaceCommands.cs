using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Ipc;
using CodeFlow.Platform;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Workspaces;

/// <summary>
/// The workspace, project and settings commands.
/// See <c>docs/business-rules/09-workspace-scoped.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// One registration file for the domain, as in <c>Git/GitCommands.cs</c>: this is the file someone
/// opens to find where a command lives, and the implementations sit one file per table.
/// </para>
/// <para>
/// The review-memory commands — <c>list_review_runs</c> and its siblings — are not here. They read
/// the table the review pipeline writes, so they sit with it, in <c>Review/ReviewCommands.cs</c>.
/// </para>
/// <para>
/// <c>pick_folder</c> is absent by design. It needs a native window to parent a modal onto, which
/// this process does not have, and the shell already exposes a picker over the renderer's dialog
/// bridge — so the renderer calls that directly instead of routing a UI concern through here.
/// </para>
/// <para>
/// Every handler runs its work through <see cref="Database"/>, which serialises access to the one
/// SQLite connection. That gate is also what keeps these off the IPC pump thread: unlike the Git
/// commands, none of them needs an explicit <see cref="Task.Run"/>, because a contended gate
/// suspends and an uncontended query on an indexed table is bounded and small.
/// </para>
/// </remarks>
public static class WorkspaceCommands
{
    public static CommandRegistry AddWorkspaceCommands(this CommandRegistry registry, Database database) =>
        registry
            // ---------- workspaces ----------
            .Add("default_clone_dir", (_, _) => ValueTask.FromResult(
                Json(AppPaths.CloneRoot, WorkspaceJsonContext.Default.String)))
            .Add("create_workspace", (p, ct) =>
            {
                var name = Arg(p, "name");
                var icon = Arg(p, "icon");
                var color = Arg(p, "color");
                return Write(database, c => WorkspaceStore.Create(c, name, icon, color),
                    WorkspaceJsonContext.Default.Workspace, ct);
            })
            .Add("list_workspaces", (_, ct) =>
                Read(database, WorkspaceStore.List, WorkspaceJsonContext.Default.ListWorkspace, ct))
            .Add("delete_workspace", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => WorkspaceStore.Delete(c, id), ct);
            })
            .Add("rename_workspace", (p, ct) =>
            {
                var id = Arg(p, "id");
                var name = Arg(p, "name");
                return WriteUnit(database, c => WorkspaceStore.Rename(c, id, name), ct);
            })
            .Add("update_workspace_color", (p, ct) =>
            {
                var id = Arg(p, "id");
                var color = Arg(p, "color");
                return WriteUnit(database, c => WorkspaceStore.UpdateColor(c, id, color), ct);
            })
            // Both nulls clear the override; both values set it. See WS-008 for the pair rule.
            .Add("update_workspace_git_identity", (p, ct) =>
            {
                var id = Arg(p, "id");
                var name = OptionalArg(p, "name");
                var email = OptionalArg(p, "email");
                return WriteUnit(database, c => WorkspaceStore.UpdateGitIdentity(c, id, name, email), ct);
            })
            // ---------- projects ----------
            .Add("create_project", (p, ct) =>
            {
                var input = Input(p);
                return Write(database, c => ProjectStore.Create(c, input),
                    WorkspaceJsonContext.Default.Project, ct);
            })
            .Add("list_projects", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return Read(database, c => ProjectStore.List(c, workspaceId),
                    WorkspaceJsonContext.Default.ListProject, ct);
            })
            .Add("get_project", (p, ct) =>
            {
                var id = Arg(p, "id");
                // Explicit T: the command resolves to null when no project carries that id, and
                // the generated type info is annotated non-nullable for the same underlying type.
                return Read<Project?>(database, c => ProjectStore.Get(c, id),
                    WorkspaceJsonContext.Default.Project!, ct);
            })
            .Add("delete_project", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => ProjectStore.Delete(c, id), ct);
            })
            .Add("move_project_to_workspace", (p, ct) =>
            {
                var id = Arg(p, "id");
                var workspaceId = Arg(p, "workspaceId");
                return WriteUnit(database, c => ProjectStore.MoveToWorkspace(c, id, workspaceId), ct);
            })
            .Add("update_project_color", (p, ct) =>
            {
                var id = Arg(p, "id");
                var color = Arg(p, "color");
                return WriteUnit(database, c => ProjectStore.UpdateColor(c, id, color), ct);
            })
            // ---------- settings ----------
            .Add("get_setting", (p, ct) =>
            {
                var key = Arg(p, "key");
                return Read<string?>(database, c => Settings.GetSetting(c, key),
                    WorkspaceJsonContext.Default.String!, ct);
            })
            .Add("set_setting", (p, ct) =>
            {
                var key = Arg(p, "key");
                var value = Arg(p, "value");
                return WriteUnit(database, c => Settings.SetSetting(c, key, value), ct);
            })
            // ---------- workspace prompts ----------
            .Add("get_workspace_prompt", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var kind = Arg(p, "kind");
                return Read(database, c => Settings.GetWorkspacePrompt(c, workspaceId, kind),
                    WorkspaceJsonContext.Default.String, ct);
            })
            .Add("set_workspace_prompt", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var kind = Arg(p, "kind");
                var content = Arg(p, "content");
                return WriteUnit(database, c => Settings.SetWorkspacePrompt(c, workspaceId, kind, content), ct);
            })
            .Add("default_workspace_prompt", (p, _) => ValueTask.FromResult(
                Json(Settings.DefaultWorkspacePrompt(Arg(p, "kind")), WorkspaceJsonContext.Default.String)))
            // ---------- SDD / Harness agents ----------
            .Add("list_workspace_agents", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return Read(database, c => WorkspaceAgentStore.List(c, workspaceId),
                    WorkspaceJsonContext.Default.ListWorkspaceAgent, ct);
            })
            .Add("upsert_workspace_agent", (p, ct) =>
            {
                var id = OptionalArg(p, "id");
                var workspaceId = Arg(p, "workspaceId");
                var name = Arg(p, "name");
                var role = Arg(p, "role");
                var provider = Arg(p, "provider");
                var model = Arg(p, "model");
                var prompt = Arg(p, "prompt");
                var enabled = Bool(p, "enabled");
                return Write(database,
                    c => WorkspaceAgentStore.Upsert(c, id, workspaceId, name, role, provider, model, prompt, enabled),
                    WorkspaceJsonContext.Default.WorkspaceAgent, ct);
            })
            .Add("delete_workspace_agent", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => WorkspaceAgentStore.Delete(c, id), ct);
            })
            // ---------- review contexts ----------
            .Add("list_review_contexts", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return Read(database, c => ReviewContextStore.List(c, workspaceId),
                    WorkspaceJsonContext.Default.ListReviewContext, ct);
            })
            .Add("upsert_review_context", (p, ct) =>
            {
                var id = OptionalArg(p, "id");
                var workspaceId = Arg(p, "workspaceId");
                var name = Arg(p, "name");
                var content = Arg(p, "content");
                var enabled = Bool(p, "enabled");
                return Write(database,
                    c => ReviewContextStore.Upsert(c, id, workspaceId, name, content, enabled),
                    WorkspaceJsonContext.Default.ReviewContext, ct);
            })
            .Add("delete_review_context", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => ReviewContextStore.Delete(c, id), ct);
            })
            // ---------- MCP servers ----------
            .Add("list_workspace_mcps", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return Read(database, c => WorkspaceMcpStore.List(c, workspaceId),
                    WorkspaceJsonContext.Default.ListWorkspaceMcp, ct);
            })
            .Add("upsert_workspace_mcp", (p, ct) =>
            {
                var id = OptionalArg(p, "id");
                var workspaceId = Arg(p, "workspaceId");
                var name = Arg(p, "name");
                var command = Arg(p, "command");
                var args = Arg(p, "args");
                var env = Arg(p, "env");
                var enabled = Bool(p, "enabled");
                return Write(database,
                    c => WorkspaceMcpStore.Upsert(c, id, workspaceId, name, command, args, env, enabled),
                    WorkspaceJsonContext.Default.WorkspaceMcp, ct);
            })
            .Add("delete_workspace_mcp", (p, ct) =>
            {
                var id = Arg(p, "id");
                return WriteUnit(database, c => WorkspaceMcpStore.Delete(c, id), ct);
            });

    // ---------- dispatch helpers ----------

    private static async ValueTask<ReadOnlyMemory<byte>> Read<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await database.ReadAsync(work, cancellationToken).ConfigureAwait(false);
        return Json(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> Write<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return Json(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> WriteUnit(
        Database database, Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return "null"u8.ToArray();
    }

    private static ReadOnlyMemory<byte> Json<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToUtf8Bytes(value, type);

    // ---------- argument helpers ----------
    //
    // Command arguments arrive camelCase, while returned shapes are snake_case. NewProject is the
    // exception and is handled in Input below.

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>An argument the renderer sends as <c>null</c> when it has no value to send.</summary>
    /// <remarks>
    /// On the upsert commands this is what distinguishes "create" from "edit": a null id mints a
    /// new row, a present one updates in place.
    /// </remarks>
    private static string? OptionalArg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>
    /// Reads <c>create_project</c>'s nested payload.
    /// </summary>
    /// <remarks>
    /// The only parameter in this feature that is a whole object, and the only one whose keys are
    /// snake_case — the camelCase rule applies to parameter lists, not to objects the renderer
    /// builds. Deserialising it through the same context the responses use is what keeps that
    /// straight.
    /// </remarks>
    private static NewProject Input(JsonElement parameters) =>
        parameters.TryGetProperty("input", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize(WorkspaceJsonContext.Default.NewProject)
              ?? throw new ArgumentException("parameter 'input' deserialised to null")
            : throw new ArgumentException("missing required parameter 'input'");
}

/// <summary>Serialisable types this feature puts on the wire.</summary>
/// <remarks>
/// <para>
/// snake_case, because <c>renderer/src/types/domain.ts</c> declares these field names
/// verbatim. A camelCase policy here would compile, serialise and then read as <c>undefined</c> in
/// every component — the failure would surface as blank rows, not as an error.
/// </para>
/// <para>
/// A per-feature context rather than entries appended to a shared one, so adding a feature never
/// means editing a file every other feature also edits.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Workspace))]
[JsonSerializable(typeof(List<Workspace>))]
[JsonSerializable(typeof(Project))]
[JsonSerializable(typeof(List<Project>))]
[JsonSerializable(typeof(NewProject))]
[JsonSerializable(typeof(ReviewContext))]
[JsonSerializable(typeof(List<ReviewContext>))]
[JsonSerializable(typeof(WorkspaceAgent))]
[JsonSerializable(typeof(List<WorkspaceAgent>))]
[JsonSerializable(typeof(WorkspaceMcp))]
[JsonSerializable(typeof(List<WorkspaceMcp>))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;
