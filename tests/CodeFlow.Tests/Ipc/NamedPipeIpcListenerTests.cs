using CodeFlow.Ipc;
using CodeFlow.Platform;
using Xunit;

namespace CodeFlow.Tests.Ipc;

/// <summary>
/// The Windows listener, and the mismatch that made the whole application inert there.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppPaths.IpcEndpoint</c> publishes <c>\\.\pipe\codeflow-&lt;pid&gt;</c>, the shell hands that string
/// straight to <c>net.connect</c>, and the listener handed the same string to
/// <c>NamedPipeServerStream</c> — which takes a bare <em>name</em> and prepends the namespace itself.
/// The server listened somewhere the client never looked. The only symptom was
/// <c>connect ENOENT \\.\pipe\codeflow-&lt;pid&gt;</c> after a 15s retry loop, and an app that started,
/// showed its window, and answered nothing.
/// </para>
/// <para>
/// Nothing in the suite could see it: all four IPC suites skipped themselves on Windows, on the
/// stated premise that this listener was "covered by running the app there".
/// </para>
/// </remarks>
public sealed class NamedPipeIpcListenerTests
{
    [Theory]
    [InlineData(@"\\.\pipe\codeflow-1234", "codeflow-1234")]
    [InlineData(@"\\?\pipe\codeflow-1234", "codeflow-1234")]
    // Case is not ours to assume: the prefix is a Windows namespace, and Windows does not care.
    [InlineData(@"\\.\PIPE\codeflow-1234", "codeflow-1234")]
    // Already a name — `--ipc-endpoint` can supply anything, and doubling the strip would corrupt it.
    [InlineData("codeflow-1234", "codeflow-1234")]
    // A macOS-shaped endpoint reaching this function would mean the platform switch is wrong; it is
    // returned untouched rather than mangled into something that half works.
    [InlineData("/Users/x/CodeFlow/.ipc-1234.sock", "/Users/x/CodeFlow/.ipc-1234.sock")]
    public void The_pipe_name_is_the_endpoint_without_its_namespace(string endpoint, string expected) =>
        Assert.Equal(expected, NamedPipeIpcListener.PipeNameFrom(endpoint));

    [Fact]
    public async Task The_published_endpoint_can_be_opened_by_the_path_it_names()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes exist on Windows only.");

        // Opened as a literal path, which is what `net.connect` does underneath. A
        // `NamedPipeClientStream` would derive the path from a bare name exactly as the server does,
        // so it would have agreed with the broken server and passed — this assertion only means
        // anything in this form.
        await using var listener = IpcListener.Create(AppPaths.IpcEndpoint(Environment.ProcessId));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(cts.Token);

        Stream? client = null;
        for (var attempt = 0; client is null; attempt++)
        {
            try
            {
                client = new FileStream(listener.Endpoint, FileMode.Open, FileAccess.ReadWrite);
            }
            catch (Exception ex) when (attempt < 50 && ex is IOException or UnauthorizedAccessException)
            {
                // The pipe instance only exists while an accept is pending, and the loop above may
                // not have reached WaitForConnection yet.
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }

        await using (client)
        {
            var accepted = await accepting;
            await accepted.DisposeAsync();
        }
    }
}
