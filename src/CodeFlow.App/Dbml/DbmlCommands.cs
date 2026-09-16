using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Ai;
using CodeFlow.Ipc;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Dbml;

/// <summary>
/// The schema designer's commands.
/// See <c>docs/business-rules/15-dbml.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The feature is deliberately thin on this side. A <c>.dbml</c> document is a file in the user's
/// folder, so opening, saving and creating one are <c>read_file_text</c>, <c>write_file_text</c> and
/// <c>create_file</c> — commands that already exist and that work without a repository. Parsing,
/// layout and export all live in the renderer, where <c>@dbml/core</c> is.
/// </para>
/// <para>
/// What is left is what the renderer cannot do: walking a folder for documents, remembering where a
/// person put each table, and running an engine over the schema.
/// </para>
/// </remarks>
public static class DbmlCommands
{
    public static CommandRegistry AddDbmlCommands(
        this CommandRegistry registry, Database database, AiRunRegistry runs, HttpClient http) =>
        registry
            .Add("dbml_list_documents", (p, ct) =>
            {
                var rootPath = Arg(p, "rootPath");
                return Run(() => DbmlDocuments.List(rootPath), DbmlJsonContext.Default.IReadOnlyListString, ct);
            })
            // ---------- layout (DBML-005) ----------
            .Add("dbml_load_layout", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var relPath = Arg(p, "relPath");
                return Read(database, c => DbmlLayoutStore.Load(c, projectId, relPath),
                    DbmlJsonContext.Default.IReadOnlyListDbmlTablePosition, ct);
            })
            .Add("dbml_save_positions", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var relPath = Arg(p, "relPath");
                var positions = Positions(p, "positions");
                return WriteUnit(database, c => DbmlLayoutStore.Save(c, projectId, relPath, positions), ct);
            })
            .Add("dbml_clear_layout", (p, ct) =>
            {
                var projectId = Arg(p, "projectId");
                var relPath = Arg(p, "relPath");
                return WriteUnit(database, c => DbmlLayoutStore.Clear(c, projectId, relPath), ct);
            })
            // ---------- the assistant (DBML-016) ----------
            .Add("dbml_assist", async (p, ct) =>
            {
                var mode = Arg(p, "mode");
                var dbml = Arg(p, "dbml");

                // Optional: a review and an explanation stand on their own, and the edit mode's own
                // requirement is enforced where the modes are told apart, not here.
                var instruction = Optional(p, "instruction") ?? string.Empty;

                var config = await database
                    .ReadAsync(c => DbmlAssistant.Bound(c, AiRouting.Resolve(c, "dbml")), ct)
                    .ConfigureAwait(false);

                var answer = await DbmlAssistant.AssistAsync(
                    AiEngineRunner.Bind(runs, http), config, mode, dbml, instruction, Run(p), ct)
                    .ConfigureAwait(false);

                return JsonSerializer.SerializeToUtf8Bytes(answer, DbmlJsonContext.Default.String);
            });

    /// <summary>
    /// The run this command belongs to, from the id the renderer minted before invoking.
    /// </summary>
    /// <remarks>
    /// Absent or blank means untracked: no <c>ai:output</c> events and no stop button, the same
    /// <see langword="null"/> run <see cref="AiCommands"/> passes.
    /// </remarks>
    private static AiRunContext? Run(JsonElement parameters) =>
        Optional(parameters, "runId") is { } runId && !string.IsNullOrWhiteSpace(runId)
            ? new AiRunContext(runId)
            : null;

    private static string? Optional(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>Reads the positions array, whose objects keep their snake_case keys.</summary>
    private static IReadOnlyList<DbmlTablePosition> Positions(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Deserialize(DbmlJsonContext.Default.IReadOnlyListDbmlTablePosition)
              ?? throw new ArgumentException($"parameter '{name}' deserialised to null")
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>
    /// Runs blocking work off the IPC pump, the rule <see cref="Git.GitCommands"/> states: there is
    /// one RPC connection for the whole application, and a walk of a large tree is not bounded.
    /// </summary>
    private static async ValueTask<ReadOnlyMemory<byte>> Run<T>(
        Func<T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var value = await Task.Run(work, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToUtf8Bytes(value, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> Read<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await database.ReadAsync(work, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToUtf8Bytes(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> WriteUnit(
        Database database, Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);

        return "null"u8.ToArray();
    }
}
