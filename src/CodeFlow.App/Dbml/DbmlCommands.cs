using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
/// What is left is what the renderer cannot do: walking a folder for documents, and remembering
/// where a person put each table.
/// </para>
/// </remarks>
public static class DbmlCommands
{
    public static CommandRegistry AddDbmlCommands(this CommandRegistry registry, Database database) =>
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
            });

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
