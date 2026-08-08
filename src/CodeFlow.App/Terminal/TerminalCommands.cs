using System.Text.Json;
using CodeFlow.Ipc;

namespace CodeFlow.Terminal;

/// <summary>The four terminal commands.</summary>
public static class TerminalCommands
{
    public static CommandRegistry AddTerminalCommands(this CommandRegistry registry, TerminalRegistry terminals) =>
        registry
            .Add("open_terminal", async (p, ct) =>
            {
                var id = await terminals.OpenAsync(Arg(p, "cwd"), ct).ConfigureAwait(false);
                return JsonSerializer.SerializeToUtf8Bytes(id, TerminalJsonContext.Default.String);
            })
            .Add("write_terminal", async (p, ct) =>
            {
                await terminals.WriteAsync(Arg(p, "id"), Arg(p, "data"), ct).ConfigureAwait(false);
                return Unit();
            })
            .Add("resize_terminal", (p, _) =>
            {
                // The frontend sends {id, cols, rows}; PtySize is built by field name, so the
                // declaration order of the two integers cannot matter.
                terminals.Resize(Arg(p, "id"), Number(p, "cols"), Number(p, "rows"));
                return ValueTask.FromResult(Unit());
            })
            .Add("close_terminal", async (p, _) =>
            {
                await terminals.CloseAsync(Arg(p, "id")).ConfigureAwait(false);
                return Unit();
            });

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static int Number(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static ReadOnlyMemory<byte> Unit() => "null"u8.ToArray();
}
