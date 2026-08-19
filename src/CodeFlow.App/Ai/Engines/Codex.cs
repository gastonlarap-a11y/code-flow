using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeFlow.Ai.Engines;

/// <summary>
/// The Codex CLI engine — OpenAI's models through a ChatGPT subscription rather than API credits.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="OpenAi"/>, and the difference is billing, not vendor: that one
/// pays per token against <c>/v1/chat/completions</c>, this one shells out to the CLI the user
/// logged into.
/// </para>
/// <para>
/// <c>codex exec</c> runs one task to completion, streams progress to <b>stderr</b> and writes only
/// the final agent message to <b>stdout</b>. That maps exactly onto this app's ask-on-argv,
/// data-on-stdin split — except the ask goes on stdin too, see <see cref="StdinPayload"/>.
/// </para>
/// </remarks>
public sealed class Codex : IAiEngine
{
    /// <summary>Single-line, ASCII, shim-safe. The real instructions arrive on stdin.</summary>
    private const string Pointer =
        "Follow the instructions in the input piped on stdin and reply with only the requested output.";

    public string Id => "codex";

    public string Label => "Codex";

    public string DefaultBinary => "codex";

    /// <inheritdoc />
    /// <remarks>
    /// Empty on purpose: which model ids a ChatGPT plan exposes depends on the subscription tier,
    /// so naming one risks picking something the account cannot use.
    /// </remarks>
    public string CommitMessageModel => string.Empty;

    /// <inheritdoc />
    /// <remarks>
    /// Empty because Codex has no tool-allowlist flag at all — write access is granted by the
    /// sandbox policy below, so there are no names to pass.
    /// </remarks>
    public IReadOnlyList<string> FixTools => [];

    /// <summary>
    /// The whole brief — system prompt, ask, then data — down the pipe.
    /// </summary>
    /// <remarks>
    /// The only engine that overrides this (<c>AI-004</c>). <c>codex exec</c>'s one positional
    /// argument is the fixed pointer sentence above, so the real instructions have to arrive on
    /// stdin. It is also the safe shape everywhere: the prompt templates are multi-line, and a CLI
    /// installed as an npm <c>.cmd</c> shim on Windows cannot receive a multi-line argument.
    /// </remarks>
    public string StdinPayload(AiInvocation invocation)
    {
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

        return brief.ToString();
    }

    public ProcessStartInfo BuildCommand(string binary, AiInvocation invocation)
    {
        var info = new ProcessStartInfo { FileName = binary };
        info.ArgumentList.Add("exec");

        // `resume <id>` is a subcommand of `exec`, so it goes between `exec` and the prompt,
        // before the flags.
        if (!string.IsNullOrWhiteSpace(invocation.ResumeSessionId))
        {
            info.ArgumentList.Add("resume");
            info.ArgumentList.Add(invocation.ResumeSessionId);
        }

        info.ArgumentList.Add(Pointer);

        if (!string.IsNullOrWhiteSpace(invocation.Model))
        {
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(invocation.Model);
        }

        // Read-only unless the flow may write. `workspace-write` is the documented successor to the
        // deprecated `--full-auto` and stays scoped to the repo; `danger-full-access` is
        // deliberately never used.
        info.ArgumentList.Add("--sandbox");
        info.ArgumentList.Add(invocation.AutoApproveEdits ? "workspace-write" : "read-only");

        // A headless run cannot answer an approval prompt — without this the agent can stop and
        // wait forever. Set through `-c` rather than `--ask-for-approval`, which `codex exec`
        // dropped: it errors with "unexpected argument" on 0.145+, while the config key works on
        // every version.
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("approval_policy=\"never\"");

        if (!string.IsNullOrWhiteSpace(invocation.Cwd))
        {
            // --cd sets the workspace root the sandbox is scoped to, so it is needed even though
            // the working directory below already points there.
            info.ArgumentList.Add("--cd");
            info.ArgumentList.Add(invocation.Cwd);
            info.WorkingDirectory = invocation.Cwd;
        }

        return info;
    }

    /// <summary>The agent's final message is stdout; the progress log is stderr.</summary>
    public AiRun Interpret(bool success, string statusLabel, string stdout, string stderr)
    {
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
            throw new AiRunFailedException($"codex exited with an error ({statusLabel}): {detail}");
        }

        var text = stdout.Trim();
        if (text.Length == 0)
        {
            var error = stderr.Trim();
            throw new AiRunFailedException(error.Length == 0 ? "codex produced no output" : error);
        }

        if (QuotaSignals.Matches(text))
        {
            throw new AiRunFailedException(QuotaSignals.Marker + text);
        }

        return new AiRun(text, SessionIdFromPreamble(stderr), Model: null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Codex has no <c>models</c> subcommand, but the CLI refreshes its own catalogue on disk —
    /// so reading that file keeps the picker current without an app release, and without paying
    /// for a process spawn.
    /// </remarks>
    public IReadOnlyList<string>? CachedModels()
    {
        var home = CodexHome();
        return home is null ? null : ReadModelsCache(home);
    }

    /// <summary>Codex's state directory: <c>$CODEX_HOME</c> when set, else <c>~/.codex</c>.</summary>
    internal static string? CodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? null : Path.Combine(home, ".codex");
    }

    /// <summary>
    /// The model catalogue Codex refreshes into <c>models_cache.json</c>.
    /// </summary>
    /// <remarks>
    /// Only entries the CLI would itself display (<c>visibility: "list"</c>) are offered, ordered
    /// by the catalogue's own <c>priority</c> so the newest lands at the top rather than
    /// alphabetically. An empty catalogue returns null rather than an empty list: for the caller
    /// those are the same thing, and null is what makes the frontend fall back to its curated list
    /// instead of showing nothing.
    /// </remarks>
    internal static IReadOnlyList<string>? ReadModelsCache(string codexHome)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(Path.Combine(codexHome, "models_cache.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        List<(string Slug, long Priority)> listed = [];
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in models.EnumerateArray())
            {
                if (!entry.TryGetProperty("slug", out var slug) || slug.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var visibility = entry.TryGetProperty("visibility", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : string.Empty;

                if (visibility != "list")
                {
                    continue;
                }

                var priority = entry.TryGetProperty("priority", out var p) && p.TryGetInt64(out var value) ? value : 0;
                listed.Add((slug.GetString()!, priority));
            }
        }
        catch (JsonException)
        {
            return null;
        }

        var slugs = listed.OrderBy(m => m.Priority).Select(m => m.Slug).ToArray();
        return slugs.Length == 0 ? null : slugs;
    }

    /// <summary>
    /// Pulls the rollout id out of <c>codex exec</c>'s stderr preamble.
    /// </summary>
    /// <remarks>
    /// The preamble prints one <c>key: value</c> per line. Matched leniently — either spelling, any
    /// case — because this is a human-readable banner, not a committed format. Null when it is not
    /// there, which costs continuity but never resumes the wrong rollout: losing a session is
    /// recoverable, silently resuming an unrelated one is not.
    /// </remarks>
    internal static string? SessionIdFromPreamble(string stderr)
    {
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            foreach (var key in new[] { "session id:", "session_id:" })
            {
                if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = line[key.Length..].Trim();
                if (id.Length > 0)
                {
                    return id;
                }
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(params string[] candidates) =>
        candidates.Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0);
}
