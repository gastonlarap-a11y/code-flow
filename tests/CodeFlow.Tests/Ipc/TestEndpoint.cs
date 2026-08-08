namespace CodeFlow.Tests.Ipc;

/// <summary>
/// A fresh IPC endpoint of the shape the app really uses, for the platform under test.
/// </summary>
/// <remarks>
/// The four IPC suites each built their own socket path inline and skipped themselves on Windows.
/// Shared here instead, so the transport a test exercises is chosen the same way
/// <see cref="CodeFlow.Platform.AppPaths.IpcEndpoint"/> chooses it — a named pipe path on Windows, a
/// socket file elsewhere — and so a fifth suite cannot quietly go back to Unix-only.
/// </remarks>
internal static class TestEndpoint
{
    public static string Create()
    {
        var id = Guid.NewGuid().ToString("N")[..12];

        // The full path, not the bare name: this stands in for what the sidecar publishes on stdout
        // and what the shell hands to `net.connect`, and keeping the two identical is the point.
        return OperatingSystem.IsWindows()
            ? $@"\\.\pipe\cf-test-{id}"
            : Path.Combine(Path.GetTempPath(), $"cf-{id}.sock");
    }
}
