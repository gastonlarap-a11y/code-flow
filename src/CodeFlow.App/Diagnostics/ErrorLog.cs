using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeFlow.Platform;

namespace CodeFlow.Diagnostics;

/// <summary>
/// Every command failure, on disk, so a problem can be reported instead of retyped.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppPaths.LogsDirectory</c> existed, was created empty on every launch, and nothing ever wrote
/// to it. The sidecar's only output was the console, which the packaged app discards — so when a
/// publish failed in the UI, the message lived in the renderer's memory and nowhere else. Reporting
/// it meant copying it by hand out of a banner that could not even be selected, and recovering it
/// afterwards meant reading SQLite's write-ahead log. Both happened.
/// </para>
/// <para>
/// Deliberately not a logging framework. One line per failure, the same text the renderer was told,
/// appended and flushed — no levels, no structure, no dependency. What it has to survive is the
/// process, and what it has to answer is "what did it say".
/// </para>
/// <para>
/// <b>Never throws.</b> A logger that can fail a command it was only meant to record would be worse
/// than the silence it replaces.
/// </para>
/// </remarks>
public static partial class ErrorLog
{
    /// <summary>Bytes kept before the file is rolled over.</summary>
    /// <remarks>
    /// One rollover, not a rotation scheme: the previous file is overwritten. A user asked for a
    /// recent error, not an archive, and unbounded growth in a directory nobody looks at is how a
    /// log becomes a disk problem.
    /// </remarks>
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly Lock Gate = new();

    /// <summary>Records one failed command.</summary>
    public static void Record(string method, Exception failure) =>
        Record(AppPaths.LogsDirectory, method, failure);

    /// <summary>
    /// Records one failed command into a named directory.
    /// </summary>
    /// <remarks>
    /// The directory is a parameter, and the whole log an injected delegate at the one call site
    /// (<see cref="Ipc.IpcServer"/>), because without either the test suite wrote its own fixtures
    /// into the user's real <c>~/CodeFlow/logs</c> — forty-five lines of <c>contoso</c> and
    /// <c>ghe.example.invalid</c> in the file that exists to tell a user what went wrong.
    /// </remarks>
    public static void Record(string directory, string method, Exception failure) =>
        Write(
            directory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {method}  {failure.GetType().Name}: {Redact(failure.Message)}"));

    /// <summary>
    /// Blanks out anything in a message that looks like a credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The messages this writes are not ours to trust. <c>GitNetwork</c> turns git's own stderr into
    /// the exception's message, and a failed <c>fetch</c> prints the remote URL it tried — which in a
    /// great many repositories carries an embedded token. Provider errors carry the response body,
    /// which can echo a header back. None of that mattered while the text lived and died in the
    /// renderer's memory; it matters now that it is a file, and a file whose whole purpose is to be
    /// sent to somebody else.
    /// </para>
    /// <para>
    /// Found by this application reviewing its own change, and it was right: <c>.claude/rules/dotnet.md</c>
    /// already says never to log token values, and the log that was added to make errors reportable
    /// broke that on its first day.
    /// </para>
    /// <para>
    /// <b>Deliberately blunt.</b> It replaces rather than detects: a false positive costs a line of
    /// diagnostics, a false negative costs a credential. What is left is the shape of the message —
    /// which host, which status, which operation — and that is what a report needs.
    /// </para>
    /// </remarks>
    internal static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var redacted = CredentialInUrl().Replace(message, "$1://***:***@");
        redacted = AuthHeader().Replace(redacted, "$1: ***");
        redacted = BareBearer().Replace(redacted, "Bearer ***");
        return TokenLiteral().Replace(redacted, "***");
    }

    /// <summary>A URL carrying <c>user:password</c>, as git prints it back on a failed exchange.</summary>
    [GeneratedRegex(@"(\w+)://[^/\s:@]+:[^/\s@]+@", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex CredentialInUrl();

    /// <summary>
    /// An auth header echoed inside an error body, scheme and value together.
    /// </summary>
    /// <remarks>
    /// The value stops at the first quote, comma or brace rather than at the first space, because
    /// these arrive embedded in JSON: a greedy run of non-space would swallow the syntax around it
    /// and turn a readable error into a broken one. The header name itself is kept — knowing that a
    /// request carried an <c>Authorization</c> at all is part of what makes the line diagnostic.
    /// </remarks>
    [GeneratedRegex(
        @"(?i)\b(authorization|x-api-key|private-token|api-key)\s*:\s*(?:bearer\s+|token\s+|basic\s+)?[^\s""',}\]]+",
        RegexOptions.None,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex AuthHeader();

    /// <summary>A <c>Bearer …</c> with no header name in front of it.</summary>
    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex BareBearer();

    /// <summary>
    /// A token recognisable on its own, by the prefixes the hosts publish.
    /// </summary>
    /// <remarks>
    /// GitHub's are documented and stable (<c>ghp_</c>, <c>gho_</c>, <c>ghu_</c>, <c>ghs_</c>,
    /// <c>ghr_</c>, <c>github_pat_</c>); Slack's and OpenAI's are here because a workspace MCP server
    /// carries them and its failures come through the same edge. Azure DevOps PATs have no prefix at
    /// all, which is exactly why the URL and header rules above matter more than this one.
    /// </remarks>
    [GeneratedRegex(
        @"\b(gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|xox[abposr]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9-]{20,})",
        RegexOptions.None,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex TokenLiteral();

    /// <summary>The file a user can be pointed at.</summary>
    public static string Path => FileIn(AppPaths.LogsDirectory);

    /// <summary>The log file inside a given directory.</summary>
    public static string FileIn(string directory) => System.IO.Path.Combine(directory, "errors.log");

    private static void Write(string directory, string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = FileIn(directory);

                if (new FileInfo(path) is { Exists: true, Length: > MaxBytes } existing)
                {
                    existing.MoveTo(path + ".1", overwrite: true);
                }

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A full disk, a read-only home, a path the OS refuses. None of them is a reason to fail
            // the command this was only recording.
        }
    }
}
