using System.Diagnostics;
using CodeFlow.Ai;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// A stand-in engine that records what it was asked and answers whatever the test told it to.
/// </summary>
/// <remarks>
/// The operations under test are exactly the code that assembles a prompt, a stdin payload and an
/// invocation, so what matters is <em>what they built</em>, not that a process ran. Driving them
/// through the <see cref="AiRunner"/> delegate keeps them honest without a subprocess: a scripted
/// process would only prove the shell echoed something back.
/// </remarks>
internal sealed class ScriptedEngine : IAiEngine
{
    private readonly AiRun _reply;
    private readonly Exception? _failure;

    private ScriptedEngine(AiRun reply, Exception? failure)
    {
        _reply = reply;
        _failure = failure;
    }

    public string Id => "scripted";

    public string Label => "Scripted";

    public string DefaultBinary => "scripted-cli";

    public bool Agentic { get; private init; } = true;

    public bool ResumesSessions { get; private init; } = true;

    public IReadOnlyList<string> FixTools { get; private init; } = ["Edit", "Write"];

    /// <summary>Every invocation this engine was handed, in order.</summary>
    public List<AiInvocation> Invocations { get; } = [];

    /// <summary>An engine that answers with <paramref name="text"/>.</summary>
    public static ScriptedEngine Answering(
        string text,
        string? sessionId = null,
        string? model = null,
        bool agentic = true,
        bool resumesSessions = true) =>
        new(new AiRun(text, sessionId, model), failure: null)
        {
            Agentic = agentic,
            ResumesSessions = resumesSessions,
        };

    /// <summary>An engine whose every run fails with <paramref name="message"/>.</summary>
    public static ScriptedEngine Failing(string message) =>
        new(new AiRun(string.Empty, null, null), new AiRunFailedException(message));

    /// <summary>The runner that drives this engine, for handing to the operations.</summary>
    public AiRunner Runner => (_, invocation, _, _) =>
    {
        Invocations.Add(invocation);
        return _failure is null ? Task.FromResult(_reply) : Task.FromException<AiRun>(_failure);
    };

    /// <summary>A config that routes to this engine.</summary>
    public AiConfig Config(string model = "scripted-model", IReadOnlyList<string>? tools = null) =>
        new(this, Id, model, DefaultBinary, tools ?? []);

    public ProcessStartInfo BuildCommand(string binary, AiInvocation invocation) =>
        throw new NotSupportedException("the scripted engine never spawns anything");

    public AiRun Interpret(bool success, string statusLabel, string stdout, string stderr) =>
        throw new NotSupportedException("the scripted engine never interprets anything");

    /// <summary>The single invocation this engine received.</summary>
    public AiInvocation Only => Invocations.Count == 1
        ? Invocations[0]
        : throw new InvalidOperationException($"expected exactly one invocation, got {Invocations.Count}");
}
