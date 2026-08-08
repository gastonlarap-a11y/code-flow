using System.Collections.Concurrent;
using System.Text.Json;
using CodeFlow.Ipc;

namespace CodeFlow.Tests.Ai;

/// <summary>Captures published events so a test can assert on what the renderer would receive.</summary>
/// <remarks>
/// Possible because features take a <see cref="PublishEvent"/> delegate rather than the whole
/// server — the seam is the thing they actually need, not a socket stood up to satisfy a test.
/// </remarks>
internal sealed class Recorder
{
    private readonly ConcurrentQueue<(string Event, string Json)> _events = new();

    public static (PublishEvent Publish, Recorder Recorded) Create()
    {
        var recorder = new Recorder();
        return (recorder.PublishAsync, recorder);
    }

    private ValueTask PublishAsync(string eventName, JsonElement payload, CancellationToken cancellationToken)
    {
        _events.Enqueue((eventName, payload.GetRawText()));
        return ValueTask.CompletedTask;
    }

    /// <summary>The <c>line</c> of every <c>ai:output</c> event for one run and stream, in order.</summary>
    /// <remarks>
    /// The field is <c>run_id</c>, spelled the way <c>events.ts:32</c> reads it. Asserting on the
    /// C# property name instead is what let the payload ship as <c>runId</c>: the test agreed with
    /// the code and both disagreed with the renderer.
    /// </remarks>
    public List<string> Lines(string runId, string stream) =>
        Payloads("ai:output")
            .Where(p => p.GetProperty("run_id").GetString() == runId
                        && p.GetProperty("stream").GetString() == stream)
            .Select(p => p.GetProperty("line").GetString() ?? string.Empty)
            .ToList();

    /// <summary>The payload of every event with this name, in order.</summary>
    public List<JsonElement> Payloads(string eventName) =>
        _events
            .Where(e => e.Event == eventName)
            .Select(e => JsonDocument.Parse(e.Json).RootElement)
            .ToList();

    /// <summary>Every event name seen, in order.</summary>
    public List<string> EventNames() => _events.Select(e => e.Event).ToList();
}
