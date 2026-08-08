using System.Text.Json;
using CodeFlow.Activity;
using CodeFlow.Ai;
using CodeFlow.Ipc;
using CodeFlow.Tests.Git;
using CodeFlow.Tests.Ipc;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// A chat turn as the renderer actually performs one: over a real socket, through the real registry,
/// against a real subprocess.
/// </summary>
/// <remarks>
/// <para>
/// The engine is a shell script that imitates Claude Code's <c>stream-json</c> output, so the run is
/// deterministic and needs no account, no network and no tokens — but everything between the wire and
/// the process is production code: dispatch, routing, the <c>PATH</c>-augmented spawn, the stdout
/// pump, the <c>ai:output</c> events, interpretation of the terminal <c>result</c> event, and the
/// persisted turn.
/// </para>
/// <para>
/// This is the test that would have caught the two bugs the slice was most exposed to: a command
/// registered under the wrong name, and an event payload the UI reads as <c>undefined</c>.
/// </para>
/// </remarks>
public sealed class AiIpcTests : IAsyncLifetime
{
    private const string Token = "ai-ipc-token";

    private TempDatabase _db = null!;
    private TempRepo _repo = null!;
    private string _binary = null!;
    private string _endpoint = null!;
    private IIpcListener _listener = null!;
    private IpcServer _server = null!;
    private HttpClient _http = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serving = null!;
    private string _projectId = null!;
    private long _nextId;

    public ValueTask InitializeAsync()
    {
        _db = new TempDatabase();
        _repo = new TempRepo();
        _repo.Write("a.txt", "before\n");
        _repo.Commit("initial", "a.txt");

        var workspace = _db.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#6366f1"));
        _projectId = _db.Use(c => ProjectStore.Create(
            c, WorkspaceStoreTests.NewProjectIn(workspace.Id) with { LocalPath = _repo.Path })).Id;

        _binary = FakeClaude();
        _db.Do(c => Settings.SetSetting(c, "claude_binary_path", _binary));

        _endpoint = Ipc.TestEndpoint.Create();
        _http = new HttpClient();
        _cts = new CancellationTokenSource();
        _listener = IpcListener.Create(_endpoint);

        // Registry first, then the server, then the commands — the same order Program.cs uses, and it
        // is not a style choice: the run registry publishes *through* the server, so the server has to
        // exist before the command that needs it can be registered.
        var registry = new CommandRegistry();
        _server = new IpcServer(registry, Token);

        registry
            .AddAiCommands(new AiRunRegistry(_server.PublishAsync), _db.Handle, _http)
            .AddActivityCommands(_db.Handle)
            .Seal();

        _serving = _server.RunAsync(_listener, _cts.Token);

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        await _server.DisposeAsync();
        await _listener.DisposeAsync();
        _cts.Dispose();
        _http.Dispose();
        _repo.Dispose();
        _db.Dispose();

        File.Delete(_binary);
    }

    [Fact]
    public async Task A_chat_turn_streams_its_log_answers_from_the_result_event_and_is_recorded()
    {
        await using var events = await ConnectAsync("stream");
        await using var client = await ConnectAsync("rpc");

        var reply = await CallAsync(client, "send_chat_message", $$"""
            {"projectId":"{{_projectId}}","message":"why is this slow?","sessionId":null,
             "conversationId":"conv-1","runId":"run-1",
             "agentProvider":null,"agentModel":null,"agentPrompt":null}
            """);

        // The answer comes from the terminal result event, never from a streamed line.
        Assert.Equal("the query is unindexed", reply.GetProperty("text").GetString());
        Assert.Equal("sess-from-cli", reply.GetProperty("session_id").GetString());
        Assert.Equal("claude-opus-4-6", reply.GetProperty("model").GetString());
        Assert.Equal("claude", reply.GetProperty("provider").GetString());
        Assert.True(reply.GetProperty("response_time_ms").GetInt64() >= 0);
        Assert.NotEmpty(reply.GetProperty("created_at").GetString()!);

        // The activity log did stream, as the intermediate events the UI dims and formats. These are
        // valid JSON events and none of them is the answer — AI-011.
        var lines = await DrainAsync(events, "ai:output");
        Assert.Contains(lines, l => l.Line.Contains("\"type\":\"system\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Line == "the query is unindexed");
        Assert.All(lines, l => Assert.Equal("run-1", l.RunId));
        Assert.All(lines, l => Assert.Equal("stdout", l.Stream));

        // And the turn survives the process, with its trace, ready for a reopened conversation.
        var turn = Assert.Single(_db.Use(c => ActivityLogStore.Messages(c, _projectId, "conv-1")));
        Assert.Equal("why is this slow?", turn.Question);
        Assert.Equal("the query is unindexed", turn.Answer);
        Assert.Equal("sess-from-cli", turn.EngineSessionId);
        Assert.False(turn.IsError);
        Assert.Contains("\"stream\":\"stdout\"", turn.Trace!, StringComparison.Ordinal);

        // Which is exactly what the history commands then serve back.
        var conversations = await CallAsync(client, "list_chat_conversations",
            $$"""{"projectId":"{{_projectId}}","search":null}""");

        var conversation = Assert.Single(conversations.EnumerateArray());
        Assert.Equal("conv-1", conversation.GetProperty("session_id").GetString());
        Assert.Equal("why is this slow?", conversation.GetProperty("title").GetString());
        Assert.Equal(1, conversation.GetProperty("turn_count").GetInt64());
    }

    [Fact]
    public async Task An_analysis_streams_under_its_job_id_and_lands_in_the_job_list()
    {
        _repo.Write("a.txt", "after\n");

        await using var events = await ConnectAsync("stream");
        await using var client = await ConnectAsync("rpc");

        var text = await CallAsync(client, "analyze_working_changes", $$"""
            {"projectId":"{{_projectId}}","jobId":"job-1",
             "agentProvider":null,"agentModel":null,"agentPrompt":null}
            """);

        // The footer is stamped by the app, not asked of the model.
        Assert.StartsWith("the query is unindexed\n\n---\n🤖 Análisis automatizado (análisis pre-commit) · Claude Code",
            text.GetString(), StringComparison.Ordinal);

        // The job id doubled as the run id, so the row the UI already renders is what streamed.
        var lines = await DrainAsync(events, "ai:output");
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Equal("job-1", l.RunId));

        var jobs = await CallAsync(client, "list_job_history", $$"""{"projectId":"{{_projectId}}"}""");
        var job = Assert.Single(jobs.EnumerateArray());

        Assert.Equal("job-1", job.GetProperty("id").GetString());
        Assert.Equal("done", job.GetProperty("status").GetString());
        Assert.Equal("Análisis de cambios", job.GetProperty("label").GetString());
        Assert.True(job.TryGetProperty("custom_label", out var custom) && custom.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Stopping_a_run_cancels_it_and_leaves_no_turn_behind()
    {
        await using var client = await ConnectAsync("rpc");
        await using var canceller = await ConnectAsync("rpc");

        // A second connection issues the stop, because the first is blocked awaiting the reply — which
        // is exactly the arrangement in the app: the command is in flight while the button is clicked.
        var turn = SendAsync(client, "send_chat_message", $$"""
            {"projectId":"{{_projectId}}","message":"take your time","sessionId":null,
             "conversationId":"conv-slow","runId":"run-slow",
             "agentProvider":null,"agentModel":null,"agentPrompt":null}
            """);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var cancelled = await CallAsync(canceller, "cancel_ai_run", """{"runId":"run-slow"}""");
            if (cancelled.GetBoolean())
            {
                break;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        var response = await turn;

        // The marker is the contract: aiRunStore.ts renders a muted note for it rather than a red
        // failure banner.
        Assert.Contains(AiRunRegistry.CancelledMarker, response.GetProperty("error").GetString()!,
            StringComparison.Ordinal);

        // AI-050: a deliberate stop is not history.
        Assert.Empty(_db.Use(c => ActivityLogStore.Messages(c, _projectId, "conv-slow")));
    }

    /// <summary>
    /// A shell script standing in for the Claude Code CLI.
    /// </summary>
    /// <remarks>
    /// Emits the same NDJSON shape: a <c>system</c>/<c>init</c> event, an intermediate
    /// <c>assistant</c> event that is valid JSON but is <em>not</em> the answer, and a terminal
    /// <c>result</c> event carrying the reply, the session id and the model. Sleeping when asked to
    /// take its time is what makes the cancellation test have something to cancel.
    /// </remarks>
    private static string FakeClaude()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-claude-{Guid.NewGuid():N}");

        File.WriteAllText(path,
            """
            #!/bin/sh
            printf '{"type":"system","subtype":"init","session_id":"sess-from-cli","model":"claude-opus-4-6"}\n'
            printf '{"type":"assistant","message":{"content":[{"type":"text","text":"let me look"}]}}\n'
            case "$*" in
              *"take your time"*) sleep 30 ;;
            esac
            printf '{"type":"result","subtype":"success","is_error":false,"result":"the query is unindexed","session_id":"sess-from-cli","modelUsage":{"claude-opus-4-6":{}}}\n'
            """);

        // Guarded rather than attributed: these tests skip on Windows, but the analyser reads call
        // sites, not xunit's skip conditions.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }

        return path;
    }

    private Task<IpcTestClient> ConnectAsync(string channel) =>
        IpcTestClient.ConnectAsync(_endpoint, channel, Token);

    /// <summary>Every payload published under <paramref name="eventName"/> that is already queued.</summary>
    private static async Task<List<(string RunId, string Stream, string Line)>> DrainAsync(
        IpcTestClient events, string eventName)
    {
        var lines = new List<(string, string, string)>();

        // The run is over by the time this is called, so every event it produced has been written;
        // the read stops as soon as the socket has nothing more to hand over.
        while (await events.TryReceiveAsync(TimeSpan.FromMilliseconds(250)) is { } frame)
        {
            if (!frame.TryGetProperty("event", out var name) || name.GetString() != eventName)
            {
                continue;
            }

            // Read by property name, the way the renderer does: the line is an escaped string inside
            // the payload, so asserting on the frame's raw text would compare against backslashes.
            var payload = frame.GetProperty("payload");
            lines.Add((
                payload.GetProperty("run_id").GetString()!,
                payload.GetProperty("stream").GetString()!,
                payload.GetProperty("line").GetString()!));
        }

        return lines;
    }

    private async Task<JsonElement> CallAsync(IpcTestClient client, string method, string parameters)
    {
        var response = await SendAsync(client, method, parameters);

        Assert.False(response.TryGetProperty("error", out var error),
            $"{method} failed: {(error.ValueKind == JsonValueKind.Undefined ? "" : error.GetString())}");

        return response.GetProperty("result");
    }

    private async Task<JsonElement> SendAsync(IpcTestClient client, string method, string parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        await client.SendAsync($$"""{"id":{{id}},"method":"{{method}}","params":{{parameters}}}""");

        var response = await client.ReceiveAsync();
        Assert.Equal(id, response.GetProperty("id").GetInt64());
        return response;
    }
}
