using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Ipc;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Workspaces;

/// <summary>The ten skill commands.</summary>
public static class SkillCommands
{
    public static CommandRegistry AddSkillCommands(
        this CommandRegistry registry,
        Database database,
        SkillInstaller installer) =>
        registry

            // ---------- the roster ----------

            .Add("list_workspace_skills", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                return Read(database, c => SkillStore.List(c, workspaceId),
                    SkillJsonContext.Default.ListWorkspaceSkill, ct);
            })
            .Add("install_workspace_skill", async (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var sourceRepo = Arg(p, "sourceRepo");
                var skillName = Arg(p, "skillName");

                // The same guard its two siblings always had (BUG-WS-b, closed): without it a
                // re-install of the same name ran npx over the shared folder and then added a
                // second row pointing at it.
                if (Directory.Exists(SkillFiles.Directory(SkillFiles.RootFor(workspaceId), skillName)))
                {
                    throw new InvalidOperationException(
                        $"A skill named \"{skillName}\" already exists in this workspace");
                }

                var installed = await installer.InstallAsync(workspaceId, sourceRepo, skillName, ct)
                    .ConfigureAwait(false);

                var row = await database
                    .WriteAsync(c => SkillStore.Add(c, workspaceId, installed, sourceRepo), ct)
                    .ConfigureAwait(false);

                return Json(row, SkillJsonContext.Default.WorkspaceSkill);
            })
            .Add("remove_workspace_skill", async (p, ct) =>
            {
                var id = Arg(p, "id");

                // Folder first, row second, and the folder's failure propagates (BUG-WS-a,
                // closed): the old order deleted the row and swallowed the filesystem error, so
                // an undeletable folder (open in an editor, locked on Windows, permissions) was
                // orphaned with no row left to find it by, permanently blocking its name. Now a
                // failed folder delete aborts before the row is touched, and the user can retry.
                await database.WriteAsync(c =>
                {
                    var skill = SkillStore.Get(c, id) ?? throw new InvalidOperationException("Skill not found");

                    SkillFiles.RemoveDirectory(SkillFiles.RootFor(skill.WorkspaceId), skill.SkillName);
                    SkillStore.Delete(c, id);
                }, ct).ConfigureAwait(false);

                return Unit();
            })
            .Add("set_workspace_skill_enabled", (p, ct) =>
            {
                var id = Arg(p, "id");
                var enabled = Bool(p, "enabled");
                return WriteUnit(database, c => SkillStore.SetEnabled(c, id, enabled), ct);
            })

            // ---------- the two creation paths that do not use npx ----------

            .Add("create_custom_skill", async (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var name = Arg(p, "name");
                var skillMd = Arg(p, "skillMd");

                var created = await Task
                    .Run(() => SkillFiles.CreateCustom(SkillFiles.RootFor(workspaceId), name, skillMd), ct)
                    .ConfigureAwait(false);

                return await AddRowAsync(database, workspaceId, created, "custom", ct).ConfigureAwait(false);
            })
            .Add("import_skill_from_folder", async (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var sourceDirectory = Arg(p, "srcDir");

                var imported = await Task
                    .Run(() => SkillFiles.ImportFromFolder(SkillFiles.RootFor(workspaceId), sourceDirectory), ct)
                    .ConfigureAwait(false);

                return await AddRowAsync(database, workspaceId, imported, "local", ct).ConfigureAwait(false);
            })

            // ---------- the in-app editor ----------

            .Add("list_skill_files", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var skillName = Arg(p, "skillName");
                return Run(() => SkillFiles.ListFiles(SkillFiles.RootFor(workspaceId), skillName),
                    SkillJsonContext.Default.IReadOnlyListString, ct);
            })
            .Add("read_skill_file", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var skillName = Arg(p, "skillName");
                var relPath = Arg(p, "relPath");
                return Run(() => SkillFiles.ReadFile(SkillFiles.RootFor(workspaceId), skillName, relPath),
                    SkillJsonContext.Default.String, ct);
            })
            .Add("write_skill_file", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var skillName = Arg(p, "skillName");
                var relPath = Arg(p, "relPath");
                var content = Arg(p, "content");
                return RunUnit(
                    () => SkillFiles.WriteFile(SkillFiles.RootFor(workspaceId), skillName, relPath, content), ct);
            })
            .Add("delete_skill_file", (p, ct) =>
            {
                var workspaceId = Arg(p, "workspaceId");
                var skillName = Arg(p, "skillName");
                var relPath = Arg(p, "relPath");
                return RunUnit(
                    () => SkillFiles.DeleteFile(SkillFiles.RootFor(workspaceId), skillName, relPath), ct);
            });

    /// <summary>Records a skill the filesystem has already created.</summary>
    private static async ValueTask<ReadOnlyMemory<byte>> AddRowAsync(
        Database database,
        string workspaceId,
        string skillName,
        string sourceRepo,
        CancellationToken cancellationToken)
    {
        var row = await database
            .WriteAsync(c => SkillStore.Add(c, workspaceId, skillName, sourceRepo), cancellationToken)
            .ConfigureAwait(false);

        return Json(row, SkillJsonContext.Default.WorkspaceSkill);
    }

    // ---------- dispatch helpers ----------

    private static async ValueTask<ReadOnlyMemory<byte>> Read<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await database.ReadAsync(work, cancellationToken).ConfigureAwait(false);
        return Json(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> WriteUnit(
        Database database, Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return Unit();
    }

    /// <summary>Runs filesystem work off the transport's thread.</summary>
    private static async ValueTask<ReadOnlyMemory<byte>> Run<T>(
        Func<T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await Task.Run(work, cancellationToken).ConfigureAwait(false);
        return Json(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> RunUnit(Action work, CancellationToken cancellationToken)
    {
        await Task.Run(work, cancellationToken).ConfigureAwait(false);
        return Unit();
    }

    private static ReadOnlyMemory<byte> Json<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToUtf8Bytes(value, type);

    private static ReadOnlyMemory<byte> Unit() => "null"u8.ToArray();

    // ---------- argument helpers ----------

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static bool Bool(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException($"missing required parameter '{name}'");
}

/// <summary>What the skills subsystem puts on the wire.</summary>
/// <remarks>
/// snake_case, so <c>skill_name</c>, <c>source_repo</c> and <c>installed_at</c> reach
/// <c>SkillsSettings.tsx</c> under the names it reads. The install event is camelCase and lives in
/// its own context beside the installer.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WorkspaceSkill))]
[JsonSerializable(typeof(List<WorkspaceSkill>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class SkillJsonContext : JsonSerializerContext;
