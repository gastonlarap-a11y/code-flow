using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeFlow.Ai.Engines;

/// <summary>
/// The opencode CLI engine.
/// </summary>
/// <remarks>
/// <para>
/// The only engine whose reply is reassembled from an event stream. <c>opencode run --format
/// json</c> is not a nicety: the session id is reported nowhere else, and without it every chat
/// turn would open a fresh conversation.
/// </para>
/// <para>
/// Its CLI reads no stdin and has no system-prompt flag, so the whole brief is attached as a file
/// and argv carries only a short pointer sentence.
/// </para>
/// </remarks>
public sealed class OpenCode : IAiEngine
{
    public string Id => "opencode";

    public string Label => "opencode";

    public string DefaultBinary => "opencode";

    /// <inheritdoc />
    /// <remarks>
    /// Empty: opencode addresses models as <c>provider/model</c>, so forcing one would name a
    /// provider the user may not have configured.
    /// </remarks>
    public string CommitMessageModel => string.Empty;

    /// <inheritdoc />
    /// <remarks>
    /// <c>AMBIGUOUS-AI-a</c>, carried over unresolved: <c>opencode run</c> has no tool-allowlist
    /// flag, so these names are never passed anywhere — write access comes from <c>--auto</c>. The
    /// reference marks the list <c>TODO(verify)</c> and keeps it for documentation; so does this.
    /// </remarks>
    public IReadOnlyList<string> FixTools => ["read", "edit", "write", "bash", "grep", "glob"];

    /// <inheritdoc />
    /// <remarks><c>opencode models</c> prints every configured <c>provider/model</c>, one per line.</remarks>
    public IReadOnlyList<string>? ListModelsArgs => ["models"];

    /// <inheritdoc />
    /// <remarks>
    /// Nothing. The brief below already carries the input, and <c>opencode run</c> never reads its
    /// stdin — so writing the payload there sent every diff twice and left a pipe nobody drained,
    /// which broke on every run and taught the runner to treat a broken pipe as normal
    /// (<c>AI-048</c>).
    /// </remarks>
    public string StdinPayload(AiInvocation invocation) => string.Empty;

    public ProcessStartInfo BuildCommand(string binary, AiInvocation invocation)
    {
        var info = new ProcessStartInfo { FileName = binary };
        info.ArgumentList.Add("run");

        var brief = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(invocation.SystemPrompt))
        {
            brief.Append(invocation.SystemPrompt).Append("\n\n");
        }

        brief.Append(invocation.Prompt);
        if (!string.IsNullOrWhiteSpace(invocation.StdinContent))
        {
            brief.Append("\n\n----- INPUT -----\n\n").Append(invocation.StdinContent);
        }

        // Short, single-line, ASCII — safe through the .cmd/cmd.exe layer. MUST come before
        // --file, which is variadic and would otherwise swallow it as another path.
        info.ArgumentList.Add(
            "The attached file contains your full instructions and the input to work on. " +
            "Follow them exactly and reply with only the requested output.");

        info.ArgumentList.Add("--format");
        info.ArgumentList.Add("json");

        if (!string.IsNullOrWhiteSpace(invocation.Model))
        {
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(invocation.Model);
        }

        if (invocation.AutoApproveEdits)
        {
            info.ArgumentList.Add("--auto");
        }

        if (!string.IsNullOrWhiteSpace(invocation.Cwd))
        {
            info.ArgumentList.Add("--dir");
            info.ArgumentList.Add(invocation.Cwd);
        }

        // Resumes *this conversation's* session by id, not whichever the CLI considers most recent.
        if (!string.IsNullOrWhiteSpace(invocation.ResumeSessionId))
        {
            info.ArgumentList.Add("--session");
            info.ArgumentList.Add(invocation.ResumeSessionId);
        }

        // --file last, so the pointer positional above cannot be mistaken for an attachment.
        if (WritePayloadFile(brief.ToString()) is { } path)
        {
            info.ArgumentList.Add("--file");
            info.ArgumentList.Add(path);
        }

        return info;
    }

    /// <summary>Writes the combined brief to a temp file so it can be attached with <c>--file</c>.</summary>
    /// <remarks>
    /// Null on a failed write: the run then proceeds with only the pointer message, which is
    /// degraded but not a crash. Lifecycle lives in <see cref="EngineScratch"/> — the runner
    /// deletes it after the invocation and the startup sweep catches orphans (BUG-AI-a, closed).
    /// </remarks>
    private static string? WritePayloadFile(string content) =>
        EngineScratch.TryWriteOpenCodePayload(content);

    public AiRun Interpret(bool success, string statusLabel, string stdout, string stderr)
    {
        if (ParseEvents(stdout) is { } parsed)
        {
            // An error event is the CLI explaining itself, and it can arrive on an otherwise
            // zero-exit run — so it is judged before the status, as in the Claude engine.
            if (parsed.Error is { } eventError)
            {
                // An error event is a failure however the process exited, which makes it one of the
                // places auth may be read: opencode's expired-token 401 arrives here on exit 0.
                throw new AiRunFailedException(
                    QuotaSignals.Matches(eventError) ? QuotaSignals.Marker + eventError
                    : AuthSignals.Matches(eventError) ? AuthSignals.Marker + eventError
                    : eventError);
            }

            var replyText = parsed.Text.Trim();
            if (!success)
            {
                var detail = FirstNonEmpty(replyText, stderr) ?? "sin salida";
                if (QuotaSignals.Matches(detail))
                {
                    throw new AiRunFailedException(QuotaSignals.Marker + detail);
                }

                if (AuthSignals.Matches(detail))
                {
                    throw new AiRunFailedException(AuthSignals.Marker + detail);
                }

                throw new AiRunFailedException(StaleSessionHint(detail)
                    ?? $"opencode exited with an error ({statusLabel}): {detail}");
            }

            if (replyText.Length == 0)
            {
                throw new AiRunFailedException("opencode produced no output");
            }

            if (QuotaSignals.Matches(replyText))
            {
                throw new AiRunFailedException(QuotaSignals.Marker + replyText);
            }

            return new AiRun(replyText, parsed.SessionId, Model: null);
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

            // After quota and inside `!success`, never over a reply that worked: a review is full
            // of the words a lost login uses, and only a failed run may be read for them.
            if (AuthSignals.Matches(stderr))
            {
                throw new AiRunFailedException(AuthSignals.Marker + stderr.Trim());
            }

            if (AuthSignals.Matches(stdout))
            {
                throw new AiRunFailedException(AuthSignals.Marker + stdout.Trim());
            }

            var detail = FirstNonEmpty(stderr, stdout) ?? "sin salida en stdout ni stderr";
            throw new AiRunFailedException(StaleSessionHint(detail)
                ?? $"opencode exited with an error ({statusLabel}): {detail}");
        }

        var text = stdout.Trim();
        if (text.Length == 0)
        {
            // Some builds print status on stderr; surface that rather than an empty reply.
            var error = stderr.Trim();
            throw new AiRunFailedException(error.Length == 0 ? "opencode produced no output" : error);
        }

        if (QuotaSignals.Matches(text))
        {
            throw new AiRunFailedException(QuotaSignals.Marker + text);
        }

        // No events means no session id. Null costs this conversation its continuity — the next
        // turn starts fresh and re-sends the project context — but never resumes the wrong one.
        return new AiRun(text, SessionId: null, Model: null);
    }

    /// <summary>
    /// Reads the event stream.
    /// </summary>
    /// <remarks>
    /// Null means stdout held no parseable event at all, so the caller falls back to treating it as
    /// plain text — which keeps a build that ignored <c>--format json</c> working exactly as before
    /// instead of reporting an empty reply. Every event carries <c>sessionID</c>, including the
    /// error ones, which is what lets a failed run still report the session it failed in.
    /// </remarks>
    internal static ParsedEvents? ParseEvents(string stdout)
    {
        List<string> texts = [];
        string? sessionId = null;
        string? error = null;
        var sawEvent = false;

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{'))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                sawEvent = true;

                if (sessionId is null &&
                    root.TryGetProperty("sessionID", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    sessionId = id.GetString();
                }

                var kind = root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null;

                switch (kind)
                {
                    // Concatenating the completed text parts reproduces exactly what the default
                    // formatter writes to a non-TTY stdout, so moving to JSON did not change the
                    // reply text.
                    case "text":
                        if (root.TryGetProperty("part", out var part) &&
                            part.TryGetProperty("text", out var partText) &&
                            partText.ValueKind == JsonValueKind.String &&
                            partText.GetString()?.Trim() is { Length: > 0 } value)
                        {
                            texts.Add(value);
                        }

                        break;

                    case "error":
                        error ??= ErrorMessage(root);
                        break;
                }
            }
        }

        return sawEvent ? new ParsedEvents(string.Join("\n", texts), sessionId, error) : null;
    }

    /// <summary>
    /// Best available description of an error event.
    /// </summary>
    /// <remarks>
    /// The message qualified by the error name when there is one: <c>APIError: Unauthorized</c>
    /// reads better in the UI than a bare message.
    /// </remarks>
    private static string? ErrorMessage(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = error.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()?.Trim()
            : null;

        var message = error.TryGetProperty("data", out var data) &&
                      data.ValueKind == JsonValueKind.Object &&
                      data.TryGetProperty("message", out var m) &&
                      m.ValueKind == JsonValueKind.String
            ? m.GetString()?.Trim()
            : null;

        return (string.IsNullOrEmpty(name), string.IsNullOrEmpty(message)) switch
        {
            (false, false) => $"{name}: {message}",
            (true, false) => message,
            (false, true) => name,
            _ => null,
        };
    }

    /// <summary>
    /// Rewrites opencode's bare "Session not found" into something actionable.
    /// </summary>
    /// <remarks>
    /// It means the id being resumed is gone from opencode's store, deleted or pruned — which the
    /// raw message does not convey. Since the app keeps re-sending that id, the conversation would
    /// otherwise look permanently broken for no visible reason. Spanish, verbatim: it is
    /// user-facing copy.
    /// </remarks>
    private static string? StaleSessionHint(string detail) =>
        detail.Contains("session not found", StringComparison.OrdinalIgnoreCase)
            ? "La sesión de opencode que continuaba esta conversación ya no existe (fue eliminada o "
              + "purgada). Inicia una conversación nueva para volver a empezar."
            : null;

    private static string? FirstNonEmpty(params string[] candidates) =>
        candidates.Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0);

    /// <summary>What one JSON-formatted run reported.</summary>
    /// <param name="Error">First error event, if any — the root cause, which later events restate.</param>
    internal sealed record ParsedEvents(string Text, string? SessionId, string? Error);
}
