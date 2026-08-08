using System.Text.Json;
using CodeFlow.Ipc;

namespace CodeFlow.Files;

/// <summary>The thirteen file commands.</summary>
public static class FileCommands
{
    public static CommandRegistry AddFileCommands(this CommandRegistry registry) =>
        registry

            // ---------- the explorer tree ----------

            .Add("list_dir", (p, ct) => Run(
                () => FileOps.ListDir(Arg(p, "repoPath"), OptionalArg(p, "subPath")),
                FileJsonContext.Default.IReadOnlyListFileEntry,
                ct))
            .Add("read_file_text", (p, ct) => Run(
                () => FileOps.ReadFileText(Arg(p, "repoPath"), Arg(p, "relPath")),
                FileJsonContext.Default.String,
                ct))
            .Add("write_file_text", (p, ct) => RunUnit(
                () => FileOps.WriteFileText(Arg(p, "repoPath"), Arg(p, "relPath"), Arg(p, "content")),
                ct))
            .Add("write_file_bytes", (p, ct) => RunUnit(
                () => FileOps.WriteFileBytes(Arg(p, "path"), Bytes(p, "contents")),
                ct))
            .Add("create_dir", (p, ct) => RunUnit(
                () => FileOps.CreateDir(Arg(p, "repoPath"), Arg(p, "relPath")),
                ct))
            .Add("create_file", (p, ct) => RunUnit(
                () => FileOps.CreateFile(Arg(p, "repoPath"), Arg(p, "relPath")),
                ct))
            .Add("move_path", (p, ct) => Run(
                () => FileOps.MovePath(Arg(p, "repoPath"), Arg(p, "fromRel"), Arg(p, "destDir")),
                FileJsonContext.Default.String,
                ct))

            // ---------- handing a path to the OS ----------

            .Add("open_in_vscode", (p, ct) => RunUnit(() => FileOps.OpenInVsCode(Arg(p, "path")), ct))
            .Add("open_in_default_app", (p, ct) => RunUnit(
                () => FileOps.OpenInDefaultApp(Arg(p, "repoPath"), Arg(p, "relPath")),
                ct))
            .Add("reveal_in_file_manager", (p, ct) => RunUnit(
                () => FileOps.RevealInFileManager(Arg(p, "path")),
                ct))

            // ---------- search and replace ----------

            .Add("list_repo_files", (p, ct) => Run(
                () => Search.ListFiles(Arg(p, "repoPath")),
                FileJsonContext.Default.IReadOnlyListString,
                ct))
            .Add("search_repo", (p, ct) => Run(
                () => Search.Find(Arg(p, "repoPath"), Arg(p, "query"), Options(p), Number(p, "maxResults")),
                FileJsonContext.Default.SearchOutcome,
                ct))
            .Add("replace_in_repo", (p, ct) => Run(
                () => Search.ReplaceAll(
                    Arg(p, "repoPath"),
                    Arg(p, "query"),
                    Arg(p, "replacement"),
                    Options(p),
                    OptionalArg(p, "onlyPath")),
                FileJsonContext.Default.ReplaceOutcome,
                ct));

    /// <summary>
    /// Runs synchronous filesystem work off the transport's thread and serialises what it answers.
    /// </summary>
    /// <remarks>
    /// A repo-wide search reads thousands of files, and the transport thread must stay free to
    /// answer other calls. The arguments are read before the hop so a missing one still fails as
    /// an argument error rather than inside the task.
    /// </remarks>
    private static async ValueTask<ReadOnlyMemory<byte>> Run<T>(
        Func<T> work,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
    {
        var value = await Task.Run(work, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToUtf8Bytes(value, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> RunUnit(Action work, CancellationToken cancellationToken)
    {
        await Task.Run(work, cancellationToken).ConfigureAwait(false);

        return Unit();
    }

    // Arguments are read by their camelCase names: that is what the renderer sends. Returned
    // shapes are snake_case — see FileJsonContext.

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>An argument the renderer sends as <c>null</c> when the user left it out.</summary>
    private static string? OptionalArg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Number(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>
    /// The find box's toggles, which the renderer sends as one camelCase object.
    /// </summary>
    /// <remarks>
    /// Absent means every toggle off and both glob lists empty, which is <c>SearchOptions</c>'s
    /// <c>Default</c> in 1.7.2.
    /// </remarks>
    private static SearchOptions Options(JsonElement parameters) =>
        parameters.TryGetProperty("options", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize(FileJsonContext.Default.SearchOptions) ?? new SearchOptions()
            : new SearchOptions();

    /// <summary>
    /// The bytes of an export, which arrive as a JSON array of numbers.
    /// </summary>
    /// <remarks>
    /// <c>CodeSnapModal.tsx</c> sends <c>Array.from(contents)</c> — a JSON array of byte values,
    /// not base64 and not a string. Changing either side alone corrupts the export.
    /// </remarks>
    private static byte[] Bytes(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"missing required parameter '{name}'");
        }

        var bytes = new byte[value.GetArrayLength()];
        var index = 0;

        foreach (var element in value.EnumerateArray())
        {
            bytes[index++] = element.GetByte();
        }

        return bytes;
    }

    private static ReadOnlyMemory<byte> Unit() => "null"u8.ToArray();
}
