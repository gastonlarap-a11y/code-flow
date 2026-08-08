using CodeFlow.Ipc;

namespace CodeFlow.Platform;

/// <summary>
/// The application-lifecycle commands.
/// See <c>docs/business-rules/02-bootstrap-platform.md</c>.
/// </summary>
/// <remarks>
/// Only the half of each that belongs to this process. Terminating the application is the shell's
/// job — it owns the window, the tray and this process's lifetime — so <c>quit_app</c> never
/// reaches here, and <c>reset_app_data</c> arrives already split: this process writes the marker,
/// and the shell quits once the call resolves.
/// </remarks>
public static class AppCommands
{
    public static CommandRegistry AddAppCommands(this CommandRegistry registry) =>
        registry.Add("reset_app_data", (_, cancellationToken) => ResetAppDataAsync(cancellationToken));

    /// <summary>
    /// Asks the <em>next</em> launch to wipe everything this application persists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is deleted here. The database is open on this process's own connection and on
    /// Windows the file is locked, so a reset that tried to delete it now would fail halfway and
    /// leave a partially wiped directory. Dropping a marker and letting startup handle it is the
    /// reference's design, and <see cref="AppPaths.ResetMarkerFile"/> is already read there.
    /// </para>
    /// <para>
    /// The OS keychain is deliberately untouched: the ADO PAT, the GitHub token and the AI API keys
    /// all survive a reset, matching the Windows uninstaller's identical scope
    /// (<c>DIVERGENCE-BOOT-b</c>). It reads like an oversight and is not one.
    /// </para>
    /// <para>
    /// The marker write is the only thing that can fail, and it must fail loudly — a caller that
    /// saw this succeed will quit the application expecting a clean directory next launch.
    /// </para>
    /// </remarks>
    private static async ValueTask<ReadOnlyMemory<byte>> ResetAppDataAsync(CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(AppPaths.ResetMarkerFile, [], cancellationToken).ConfigureAwait(false);
        return "null"u8.ToArray();
    }
}
