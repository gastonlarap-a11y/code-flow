namespace CodeFlow.Ai;

/// <summary>
/// One headless invocation, described in provider-neutral terms.
/// </summary>
/// <remarks>
/// <para>
/// The prompt/stdin split is the load-bearing part: <b>the ask goes on argv, the data goes on
/// stdin</b>. That is what lets one set of templates work across engines whose CLIs differ wildly
/// in argv length limits and shell escaping — a diff pasted into an argument would break several
/// of them. <see cref="IAiEngine.StdinPayload"/> is the escape hatch for the one engine whose CLI
/// reads its instructions from stdin instead.
/// </para>
/// <para>
/// See <c>docs/business-rules/05-ai-engines.md</c> <c>AI-002</c>.
/// </para>
/// </remarks>
/// <param name="Prompt">The ask. Passed as an argument by most engines.</param>
/// <param name="StdinContent">The data — a diff, PR context, a finding, the sides of a conflict.</param>
/// <param name="SystemPrompt">Extra instructions appended for this run.</param>
/// <param name="Model">Model id to force. Empty means "let the CLI pick its own default".</param>
/// <param name="AllowedTools">Raw, provider-specific tool names, passed through verbatim.</param>
/// <param name="Cwd">Working directory to run in.</param>
/// <param name="McpConfigPath">Path to a <c>--mcp-config</c>-style JSON file, when the workspace has MCP servers enabled.</param>
/// <param name="ResumeSessionId">Session to resume, for multi-turn chat.</param>
/// <param name="AutoApproveEdits">
/// Semantic "auto-approve file create/edit tools". Each engine maps it to its own permission
/// concept, because a headless run has no TTY to answer an interactive prompt on — without it the
/// write-capable flows would hang rather than fail.
/// </param>
public sealed record AiInvocation(
    string Prompt,
    string StdinContent,
    string? SystemPrompt = null,
    string Model = "",
    IReadOnlyList<string>? AllowedTools = null,
    string? Cwd = null,
    string? McpConfigPath = null,
    string? ResumeSessionId = null,
    bool AutoApproveEdits = false)
{
    /// <summary>The tools this run may use, never null.</summary>
    public IReadOnlyList<string> Tools => AllowedTools ?? [];
}

/// <summary>How an engine actually reaches its model.</summary>
/// <remarks>
/// The variant routes the call before anything subprocess-specific happens — binary resolution,
/// <c>PATH</c> building, stdio pipes. The two HTTP engines never reach
/// <see cref="IAiEngine.BuildCommand"/> or <see cref="IAiEngine.Interpret"/> at all
/// (<c>AI-003</c>). It also carries the credential, so no operation signature has to grow an
/// api-key parameter and no key is ever passed to a child process.
/// </remarks>
public abstract record Transport
{
    private Transport()
    {
    }

    /// <summary>A headless CLI child process — the default.</summary>
    public sealed record Subprocess : Transport
    {
        public static readonly Subprocess Instance = new();
    }

    /// <summary>A local Ollama server. No credential.</summary>
    public sealed record Ollama : Transport
    {
        public static readonly Ollama Instance = new();
    }

    /// <summary>Any endpoint speaking OpenAI's <c>/v1/chat/completions</c>.</summary>
    public sealed record OpenAiCompatible(string ApiKey) : Transport;
}
