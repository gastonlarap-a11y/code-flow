using System.Text.Json;

namespace CodeFlow.Ipc;

/// <summary>Handles one command: raw arguments in, a serialised result out.</summary>
/// <remarks>
/// The result is returned already serialised so each feature keeps control of its own
/// source-generated type info, rather than a central dispatcher needing to know every DTO.
/// </remarks>
public delegate ValueTask<ReadOnlyMemory<byte>> CommandHandler(
    JsonElement parameters,
    CancellationToken cancellationToken);

/// <summary>
/// The name-to-handler table the shell's RPC channel dispatches through.
/// </summary>
/// <remarks>
/// <para>
/// The 220 commands are contributed per feature rather than listed in one block: each folder
/// exposes an <c>Add…Commands</c> extension method next to its handlers, and the composition root
/// calls them. A single 220-entry switch in one file would be the one place every feature has to
/// touch, which is exactly what "a feature's code in one place" is trying to avoid.
/// </para>
/// <para>
/// Registration happens once at startup, before the transport accepts a connection, so lookups
/// afterwards are read-only and need no locking.
/// </para>
/// </remarks>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandHandler> _handlers = new(StringComparer.Ordinal);
    private bool _sealed;

    public int Count => _handlers.Count;

    public IEnumerable<string> Names => _handlers.Keys;

    /// <summary>Registers a command under the exact name the frontend invokes.</summary>
    /// <remarks>
    /// Names are the snake_case strings from <c>docs/business-rules/01-ipc-surface.md</c> and are
    /// a contract with the renderer — <c>get_status</c>, not <c>GetStatus</c>.
    /// </remarks>
    public CommandRegistry Add(string name, CommandHandler handler)
    {
        ObjectDisposedException.ThrowIf(_sealed, this);

        if (!_handlers.TryAdd(name, handler))
        {
            // Two features claiming one name would make which one runs depend on registration
            // order. CodeFlow 1.7.2 has no duplicate command names and neither should this.
            throw new InvalidOperationException($"command '{name}' is already registered");
        }

        return this;
    }

    /// <summary>Closes the registry to further registration.</summary>
    public CommandRegistry Seal()
    {
        _sealed = true;
        return this;
    }

    public bool TryGet(string name, out CommandHandler handler) => _handlers.TryGetValue(name, out handler!);
}
