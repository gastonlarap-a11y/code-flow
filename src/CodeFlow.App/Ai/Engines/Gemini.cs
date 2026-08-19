using System.Diagnostics;
using System.Text;

namespace CodeFlow.Ai.Engines;

/// <summary>
/// The Gemini engine, which drives <c>agy</c> — the Antigravity CLI.
/// </summary>
/// <remarks>
/// <para>
/// The provider id is <c>gemini</c> but the binary is <c>agy</c>; that mismatch is 1.7.2's
/// and is user-visible in the settings field, so it is preserved rather than tidied.
/// </para>
/// <para>
/// <c>agy -p</c> does not read stdin, so the whole brief goes on argv — or, past a size limit, into
/// a temp file the CLI is pointed at. It also gives a headless caller no conversation id, which is
/// why resume here is a blunt "continue the last one".
/// </para>
/// </remarks>
public sealed class Gemini : IAiEngine
{
    /// <summary>
    /// Stand-in for a session id, because agy never tells a <c>-p</c> caller its real one.
    /// </summary>
    /// <remarks>
    /// It identifies nothing. Its only job is to keep the app's chat state at "there is a session"
    /// so the next turn passes something, which <see cref="BuildCommand"/> turns into
    /// <c>--continue</c>. Being a fixed string is why chat turns group under the app's own
    /// conversation id and never under this one.
    /// </remarks>
    internal const string SessionSentinel = "agy-last";

    /// <summary>
    /// Above this many characters the brief goes to a temp file instead of onto argv.
    /// </summary>
    /// <remarks>
    /// To stay clear of the Windows ~32k command-line limit: a review diff alone can reach 120k.
    /// </remarks>
    private const int InlineLimit = 12_000;

    public string Id => "gemini";

    public string Label => "Gemini";

    public string DefaultBinary => "agy";

    /// <inheritdoc />
    /// <remarks>
    /// Empty: agy's model ids depend on the account's quota and availability, so naming one risks
    /// pointing at something the user's plan does not expose.
    /// </remarks>
    public string CommitMessageModel => string.Empty;

    /// <inheritdoc />
    /// <remarks>
    /// Empty because agy has no tool-allowlist flag; write access comes from
    /// <c>--dangerously-skip-permissions</c>.
    /// </remarks>
    public IReadOnlyList<string> FixTools => [];

    /// <inheritdoc />
    /// <remarks><c>agy models</c> prints one model id per line, which is exactly what listing wants.</remarks>
    public IReadOnlyList<string>? ListModelsArgs => ["models"];

    /// <inheritdoc />
    /// <remarks>
    /// Nothing. <see cref="ComposeBrief"/> already carries the input, inline or through the temp
    /// file agy is told to read — so writing the payload to stdin as well sent every diff twice and
    /// left a pipe nobody drained, which broke on every run and taught the runner to treat a broken
    /// pipe as normal (<c>AI-048</c>).
    /// </remarks>
    public string StdinPayload(AiInvocation invocation) => string.Empty;

    public ProcessStartInfo BuildCommand(string binary, AiInvocation invocation)
    {
        var info = new ProcessStartInfo { FileName = binary };
        var brief = ComposeBrief(invocation);

        // Deliver the brief inline when it fits, else via a temp file agy is told to read.
        var written = WriteBriefFileIfLarge(brief);
        var needsReadPermission = written is not null;

        info.ArgumentList.Add("-p");
        if (written is var (directory, file))
        {
            info.ArgumentList.Add(
                $"Read the file at {file} and carry out the instructions it contains, replying with only the requested output.");
            info.ArgumentList.Add("--add-dir");
            info.ArgumentList.Add(directory);
        }
        else
        {
            info.ArgumentList.Add(brief);
        }

        if (!string.IsNullOrWhiteSpace(invocation.Model))
        {
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(invocation.Model);
        }

        // Skip permission prompts when the flow may write, or when agy has to read the temp brief
        // headlessly. A small read-only prompt needs neither.
        if (invocation.AutoApproveEdits || needsReadPermission)
        {
            info.ArgumentList.Add("--dangerously-skip-permissions");
        }

        // Resumes the most recent conversation — not *this* one. agy gives a headless caller no id
        // to be specific with, so two chats on one project can cross. Documented, not fixable here.
        if (!string.IsNullOrWhiteSpace(invocation.ResumeSessionId))
        {
            info.ArgumentList.Add("--continue");
        }

        if (!string.IsNullOrWhiteSpace(invocation.Cwd))
        {
            info.WorkingDirectory = invocation.Cwd;
        }

        return info;
    }

    /// <summary>System instructions, then the ask, then the data.</summary>
    internal static string ComposeBrief(AiInvocation invocation)
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

    /// <summary>
    /// Writes an oversized brief to a temp file, or returns null when it fits on argv.
    /// </summary>
    /// <remarks>
    /// A per-call subdirectory, so <c>--add-dir</c> scopes agy to exactly this file and nothing
    /// else. A failed write also returns null, degrading to an inline attempt rather than failing
    /// the call outright.
    /// </remarks>
    /// <returns>The directory to grant and the file to read, or null.</returns>
    internal static (string Directory, string File)? WriteBriefFileIfLarge(string content)
    {
        if (content.Length <= InlineLimit)
        {
            return null;
        }

        // Lifecycle lives in EngineScratch: the runner deletes the directory after the
        // invocation and the startup sweep catches orphans (BUG-AI-a, closed — 1.7.2's own
        // comment admitted "Temp files aren't cleaned up yet").
        return EngineScratch.TryWriteAgyBrief(content);
    }

    /// <summary>agy prints the reply to stdout; banners and status go to stderr.</summary>
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
            // of the words a lost login uses, and only a failed run may be read for them. agy has
            // no captured logged-out payload yet, so this rides on the wording it shares with the
            // other three rather than on anything measured.
            if (AuthSignals.Matches(stderr))
            {
                throw new AiRunFailedException(AuthSignals.Marker + stderr.Trim());
            }

            if (AuthSignals.Matches(stdout))
            {
                throw new AiRunFailedException(AuthSignals.Marker + stdout.Trim());
            }

            var detail = FirstNonEmpty(stderr, stdout) ?? "sin salida en stdout ni stderr";
            throw new AiRunFailedException($"agy exited with an error ({statusLabel}): {detail}");
        }

        var text = stdout.Trim();
        if (text.Length == 0)
        {
            var error = stderr.Trim();
            throw new AiRunFailedException(error.Length == 0 ? "agy produced no output" : error);
        }

        if (QuotaSignals.Matches(text))
        {
            throw new AiRunFailedException(QuotaSignals.Marker + text);
        }

        return new AiRun(text, SessionSentinel, Model: null);
    }

    private static string? FirstNonEmpty(params string[] candidates) =>
        candidates.Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0);
}
