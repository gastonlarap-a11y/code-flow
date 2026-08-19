using System.Diagnostics;
using System.Text.Json;

namespace CodeFlow.Ai.Engines;

/// <summary>
/// The Claude Code CLI engine.
/// </summary>
/// <remarks>
/// Only what is specific to the <c>claude</c> CLI: its flags, and how it reports results under
/// <c>--output-format stream-json</c>. Everything provider-neutral lives in the shared AI layer.
/// </remarks>
public sealed class Claude : IAiEngine
{
    public string Id => "claude";

    public string Label => "Claude Code";

    public string DefaultBinary => "claude";

    /// <summary>
    /// Commit messages always run on Haiku, regardless of the configured review model.
    /// </summary>
    /// <remarks>
    /// A small, mechanical task that does not need a bigger model. Note this step exists only for
    /// this engine — the others define it empty — and the frontend's own routing preview omits it,
    /// which is <c>BUG-XLANG-a</c>.
    /// </remarks>
    public string CommitMessageModel => "claude-haiku-4-5-20251001";

    /// <inheritdoc />
    /// <remarks>The write-capable set for "fix with AI"; clicking fix is itself the opt-in.</remarks>
    public IReadOnlyList<string> FixTools => ["Read", "Edit", "Write", "Grep", "Glob"];

    /// <summary>Builds the invocation.</summary>
    /// <remarks>
    /// <c>stream-json</c> emits one JSON event per line <em>as the run happens</em> instead of a
    /// single blob at the end, which is what the activity log shows. The CLI only accepts it
    /// alongside <c>--verbose</c> in <c>-p</c> mode. The final <c>result</c> event carries exactly
    /// the payload the plain <c>json</c> format produced, so interpretation reads the same fields.
    /// </remarks>
    public ProcessStartInfo BuildCommand(string binary, AiInvocation invocation)
    {
        var info = new ProcessStartInfo { FileName = binary };
        info.ArgumentList.Add("-p");
        info.ArgumentList.Add(invocation.Prompt);

        if (!string.IsNullOrWhiteSpace(invocation.SystemPrompt))
        {
            info.ArgumentList.Add("--append-system-prompt");
            info.ArgumentList.Add(invocation.SystemPrompt);
        }

        if (!string.IsNullOrWhiteSpace(invocation.Model))
        {
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(invocation.Model);
        }

        info.ArgumentList.Add("--output-format");
        info.ArgumentList.Add("stream-json");
        info.ArgumentList.Add("--verbose");

        // Only the user's own settings, never the analysed repository's.
        //
        // The CLI runs with that repository as its working directory, so without this it loads the
        // repository's `.claude/settings.json` and runs *its* hooks inside CodeFlow's review. A
        // `Stop` hook that type-checks and lints then holds the process open long after the review
        // itself is done — observed as a one-file analysis sitting at "working" for five minutes.
        // The reasoning is `--strict-mcp-config`'s, one step further down: a run CodeFlow started
        // should be shaped by CodeFlow and the user, not by whatever the repository under review
        // happens to configure. `user` also excludes `settings.local.json`, which is the same
        // repository wearing a different file name.
        info.ArgumentList.Add("--setting-sources");
        info.ArgumentList.Add("user");

        if (invocation.AllowedTools is { } requested)
        {
            // Two flags, because they answer two different questions — and confusing them is why an
            // earlier attempt to bound a review's cost changed nothing at all.
            //
            //   --tools         which tools exist for this run at all
            //   --allowedTools  which of them run without asking for approval
            //
            // Passing only the second left `Bash` available and merely un-approved, and in headless
            // mode it went on being called: seventeen times in one measured review, against two
            // `Read`s. The list is what the run may reach for, so it is what `--tools` receives; the
            // same list is auto-approved, because a headless run has nobody to ask.
            //
            // An empty list is a decision and not the absence of one: the CLI documents `--tools ""`
            // as "disable all tools", which is what a review that was already handed the code around
            // every change asks for. Nothing is auto-approved in that case because nothing can run.
            var tools = string.Join(",", requested);
            info.ArgumentList.Add("--tools");
            info.ArgumentList.Add(tools);

            if (requested.Count > 0)
            {
                info.ArgumentList.Add("--allowedTools");
                info.ArgumentList.Add(tools);
            }
        }

        if (invocation.AutoApproveEdits)
        {
            info.ArgumentList.Add("--permission-mode");
            info.ArgumentList.Add("acceptEdits");
        }

        if (!string.IsNullOrWhiteSpace(invocation.McpConfigPath))
        {
            // --strict-mcp-config rides along so the run uses only the servers this workspace
            // enabled, ignoring whatever the user's own global CLI config happens to define.
            info.ArgumentList.Add("--mcp-config");
            info.ArgumentList.Add(invocation.McpConfigPath);
            info.ArgumentList.Add("--strict-mcp-config");
        }

        if (!string.IsNullOrWhiteSpace(invocation.ResumeSessionId))
        {
            info.ArgumentList.Add("--resume");
            info.ArgumentList.Add(invocation.ResumeSessionId);
        }

        if (!string.IsNullOrWhiteSpace(invocation.Cwd))
        {
            info.WorkingDirectory = invocation.Cwd;
        }

        return info;
    }

    /// <summary>
    /// Turns one finished run into its reply, or an error message for the frontend.
    /// </summary>
    /// <remarks>
    /// <b>stdout is parsed before the exit status is judged.</b> The CLI reports its own failures
    /// on stdout as <c>{"is_error":true,"result":"…"}</c> and exits non-zero leaving stderr
    /// <em>empty</em>. Branching on the status first and reporting stderr discards the only copy
    /// of the reason — expired auth, unknown model — and leaves the user staring at a bare
    /// "claude exited with an error:" with nothing after it.
    /// </remarks>
    public AiRun Interpret(bool success, string statusLabel, string stdout, string stderr)
    {
        var parsed = FindResultPayload(stdout);
        var text = parsed?.Result?.Trim();

        if (!string.IsNullOrEmpty(text))
        {
            if (QuotaSignals.Matches(text))
            {
                throw new AiRunFailedException(QuotaSignals.Marker + text);
            }

            if (!success || parsed!.IsError)
            {
                // Asked only once the payload itself has said the run failed. `AuthSignals` must
                // never see the text of a run that worked, or a finding about a 401 turns a
                // finished review into a login error.
                throw new AiRunFailedException(
                    AuthSignals.Matches(text) ? AuthSignals.Marker + text : text);
            }

            return new AiRun(text, parsed!.SessionId, ModelUsed(parsed), parsed.Usage);
        }

        if (!success)
        {
            if (QuotaSignals.Matches(stderr))
            {
                throw new AiRunFailedException(QuotaSignals.Marker + stderr.Trim());
            }

            if (QuotaSignals.Matches(stdout))
            {
                throw new AiRunFailedException(QuotaSignals.Marker + stdout.Trim());
            }

            if (AuthSignals.Matches(stderr))
            {
                throw new AiRunFailedException(AuthSignals.Marker + stderr.Trim());
            }

            if (AuthSignals.Matches(stdout))
            {
                throw new AiRunFailedException(AuthSignals.Marker + stdout.Trim());
            }

            // Neither stream carried a usable message — report the exit status rather than an
            // error string that trails off into nothing. The fallback text is Spanish and stays
            // Spanish: it is user-facing copy from 1.7.2, not a log line.
            var detail = FirstNonEmpty(stderr, stdout) ?? "sin salida en stdout ni stderr";
            throw new AiRunFailedException($"claude exited with an error ({statusLabel}): {detail}");
        }

        // A successful run whose stdout was not the expected envelope: treat the raw output as
        // the reply rather than discarding it. This is what keeps an older CLI, or one that
        // ignored the format flag, working.
        var fallback = stdout.Trim();
        if (fallback.Length == 0)
        {
            throw new AiRunFailedException("claude produced no output");
        }

        if (QuotaSignals.Matches(fallback))
        {
            throw new AiRunFailedException(QuotaSignals.Marker + fallback);
        }

        return new AiRun(fallback, null, null);
    }

    /// <summary>
    /// Picks the payload to interpret out of stdout.
    /// </summary>
    /// <remarks>
    /// Under <c>stream-json</c> stdout is one JSON event per line and the <em>last</em>
    /// <c>{"type":"result",…}</c> is the run's verdict. Falling back to parsing the whole buffer
    /// keeps a CLI that ignored, or does not know, the flag working exactly as before — so this
    /// is safe against both older and newer versions.
    /// </remarks>
    internal static ClaudeResult? FindResultPayload(string stdout)
    {
        var lines = stdout.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "result")
                {
                    return Read(document.RootElement);
                }
            }
            catch (JsonException)
            {
                // Not every line is JSON; the CLI is free to print anything it likes.
            }
        }

        try
        {
            using var whole = JsonDocument.Parse(stdout);
            return Read(whole.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClaudeResult Read(JsonElement element) => new()
    {
        Result = element.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null,
        IsError = element.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True,
        SessionId = element.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String
            ? session.GetString()
            : null,
        ModelUsage = element.TryGetProperty("modelUsage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? usage.EnumerateObject().Select(p => p.Name).ToArray()
            : [],
        Usage = ReadUsage(element),
    };

    /// <summary>
    /// The model the CLI actually used, when it is unambiguous.
    /// </summary>
    /// <remarks>
    /// Token accounting is keyed by model id, which is the only way to report a concrete version
    /// when no <c>--model</c> was passed and the CLI chose for itself. More than one key means
    /// the run spanned models, so no single answer is honest.
    /// </remarks>
    private static string? ModelUsed(ClaudeResult parsed) =>
        parsed.ModelUsage.Length == 1 ? parsed.ModelUsage[0] : null;

    private static string? FirstNonEmpty(params string[] candidates) =>
        candidates.Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0);

    internal sealed record ClaudeResult
    {
        public string? Result { get; init; }

        public bool IsError { get; init; }

        public string? SessionId { get; init; }

        public string[] ModelUsage { get; init; } = [];

        /// <summary>What the run consumed, or null when the event carried no <c>usage</c>.</summary>
        public AiUsage? Usage { get; init; }
    }

    /// <summary>
    /// Reads the <c>usage</c> block and the two figures beside it.
    /// </summary>
    /// <remarks>
    /// Field names taken from a real <c>result</c> event, pinned in the tests rather than recalled:
    /// <c>input_tokens</c>, <c>output_tokens</c>, <c>cache_read_input_tokens</c> and
    /// <c>cache_creation_input_tokens</c>, with <c>total_cost_usd</c> and <c>duration_ms</c> at the
    /// top level. An event without <c>usage</c> yields null, so an engine that reports nothing is
    /// never shown as a run that cost nothing.
    /// </remarks>
    private static AiUsage? ReadUsage(JsonElement element)
    {
        if (!element.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new AiUsage(
            Count(usage, "input_tokens"),
            Count(usage, "output_tokens"),
            Count(usage, "cache_read_input_tokens"),
            Count(usage, "cache_creation_input_tokens"),
            element.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number
                ? cost.GetDouble()
                : null,
            element.TryGetProperty("duration_ms", out var duration) && duration.ValueKind == JsonValueKind.Number
                ? duration.GetInt64()
                : null);
    }

    private static long Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
}
