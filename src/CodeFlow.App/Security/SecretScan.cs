using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeFlow.Git;

namespace CodeFlow.Security;

/// <summary>A single credential-looking match found in the staged diff.</summary>
/// <param name="File">Repo-relative path of the file the match is in.</param>
/// <param name="Line">1-based line number in the new file (0 if libgit2 did not report one).</param>
/// <param name="Rule">Stable rule id (e.g. <c>github-token</c>) — safe to match on in the UI.</param>
/// <param name="RuleName">Human-readable label (technical proper nouns, left untranslated).</param>
/// <param name="Severity"><c>critical</c> or <c>warning</c> — drives the severity colour in the UI.</param>
/// <param name="Preview">Masked snippet: enough to recognise the value, not enough to leak it.</param>
public sealed record SecretHit(
    string File,
    uint Line,
    string Rule,
    string RuleName,
    string Severity,
    string Preview);

/// <summary>
/// The deterministic secret scanner behind the pre-commit gate.
/// See <c>docs/business-rules/10-security.md</c>, <c>SEC-008</c>–<c>SEC-013</c>.
/// </summary>
/// <remarks>
/// <para>
/// Intentionally <b>not</b> the credential store, which keeps the user's own tokens in the OS
/// keychain. This looks at the staged diff and flags credentials the user is about to commit.
/// </para>
/// <para>
/// Three design choices carry over verbatim. <b>Only added lines</b> are scanned: a secret sitting
/// in a context line was already in the repository, and this gate is about what <em>this</em> commit
/// introduces. <b>Regex, not AI</b>: fast, offline, free, and deterministic — no false "looks clean"
/// from a model. And <b>at most one hit per line</b>, which keeps the report readable and makes the
/// declaration order of the rules load-bearing, because the first match wins.
/// </para>
/// </remarks>
public static partial class SecretScan
{
    /// <summary>
    /// Every rule, in declaration order.
    /// </summary>
    /// <remarks>
    /// The order is behaviour, not tidiness: a line matching two rules is reported under whichever
    /// comes first, so the generic assignment rule stays last where it can only catch what nothing
    /// more specific claimed.
    /// </remarks>
    private static readonly Rule[] Rules =
    [
        new("private-key", "Private key (PEM)", "critical", PrivateKey()),
        new("aws-access-key", "AWS access key id", "critical", AwsAccessKey()),
        new("aws-secret-key", "AWS secret access key", "critical", AwsSecretKey()),
        new("github-token", "GitHub token", "critical", GitHubToken()),
        new("github-pat", "GitHub fine-grained PAT", "critical", GitHubPat()),
        new("google-api-key", "Google API key", "critical", GoogleApiKey()),
        new("slack-token", "Slack token", "critical", SlackToken()),
        new("slack-webhook", "Slack webhook URL", "warning", SlackWebhook()),
        new("stripe-secret-key", "Stripe secret key", "critical", StripeSecretKey()),
        new("stripe-restricted-key", "Stripe restricted key", "critical", StripeRestrictedKey()),
        new("openai-key", "OpenAI API key", "critical", OpenAiKey()),
        new("npm-token", "npm access token", "critical", NpmToken()),
        new("azure-storage-key", "Azure storage account key", "critical", AzureStorageKey()),
        new("jwt", "JSON Web Token (JWT)", "warning", Jwt()),
        new("hardcoded-secret", "Hardcoded secret assignment", "warning", HardcodedSecret(), CheckPlaceholder: true),
    ];

    /// <summary>
    /// Values that look like templates or examples rather than real secrets.
    /// </summary>
    /// <remarks>
    /// Cuts most of the noise from the generic assignment rule
    /// (<c>token = "your-token-here"</c>, <c>secret = "${ENV_VAR}"</c>). <c>VERBATIM</c>.
    /// </remarks>
    private static readonly string[] Needles =
        ["example", "changeme", "placeholder", "your-", "your_", "yourtoken", "xxxx", "todo", "<"];

    /// <summary>Scans the staged diff of a repository (<c>SEC-013</c>).</summary>
    /// <remarks>
    /// The only thing that can fail is reading the diff; <see cref="ScanDiff"/> itself cannot.
    /// </remarks>
    public static IReadOnlyList<SecretHit> ScanStaged(string repoPath) => ScanDiff(Diff.Staged(repoPath));

    /// <summary>Scans the added lines of a staged diff and returns every credential-looking match.</summary>
    public static IReadOnlyList<SecretHit> ScanDiff(IReadOnlyList<FileDiffInfo> files)
    {
        var hits = new List<SecretHit>();

        foreach (var file in files)
        {
            var path = file.NewPath ?? file.OldPath ?? "?";

            foreach (var line in file.Hunks.SelectMany(h => h.Lines))
            {
                // Only newly-added content — context and removed lines are not what is being
                // committed.
                if (line.Origin != "+")
                {
                    continue;
                }

                foreach (var rule in Rules)
                {
                    var match = rule.Pattern.Match(line.Content);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var group = match.Groups["val"];
                    var value = group.Success ? group.Value : match.Value;

                    if (rule.CheckPlaceholder && IsPlaceholder(value))
                    {
                        continue;
                    }

                    hits.Add(new SecretHit(
                        path,
                        (uint)(line.NewLineno ?? 0),
                        rule.Id,
                        rule.Name,
                        rule.Severity,
                        Mask(value)));

                    // One hit per line keeps the report readable.
                    break;
                }
            }
        }

        return hits;
    }

    internal static bool IsPlaceholder(string value)
    {
        if (value.Contains("${", StringComparison.Ordinal)
            || value.Contains("{{", StringComparison.Ordinal)
            || value.Contains("process.env", StringComparison.Ordinal)
            || value.Contains("os.environ", StringComparison.Ordinal)
            || value.Contains("getenv", StringComparison.Ordinal))
        {
            return true;
        }

        var lower = value.ToLowerInvariant();

        return Needles.Any(n => lower.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>Masks a matched value so the report shows its shape without exposing it.</summary>
    /// <remarks>
    /// Every count here is in Unicode scalar values, never UTF-16 units. Using
    /// <see cref="string.Length"/> and <see cref="string.Substring(int, int)"/> instead would
    /// mis-measure any non-BMP character and could slice a surrogate pair in half — in a string
    /// whose entire purpose is to be shown to a human.
    /// </remarks>
    internal static string Mask(string matched)
    {
        var trimmed = matched.Trim();
        var runes = trimmed.EnumerateRunes().ToArray();
        var n = runes.Length;

        if (n <= 6)
        {
            return new string('•', Math.Max(n, 3));
        }

        var head = string.Concat(runes.Take(3));
        var tail = string.Concat(runes.Skip(n - 2));
        var dots = Math.Min(n - 5, 16);

        return $"{head}{new string('•', dots)}{tail}";
    }

    /// <param name="CheckPlaceholder">
    /// When true, the matched value is run through <see cref="IsPlaceholder"/> and skipped if it
    /// looks like a template rather than a real secret. Only the noisy generic rule sets this.
    /// </param>
    private sealed record Rule(
        string Id,
        string Name,
        string Severity,
        Regex Pattern,
        bool CheckPlaceholder = false);

    // The fifteen patterns. These are byte-level contracts (see
    // docs/business-rules/13-cross-language-contracts.md): the inline `(?i)` flags and the `\b`
    // boundaries are part of the pattern and changing one changes what the gate catches. The two
    // rules that capture a value use `(?<val>...)`, .NET's spelling of a named group.

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"(?i)aws_secret_access_key\s*[:=]\s*['""]?(?<val>[A-Za-z0-9/+=]{40})['""]?")]
    private static partial Regex AwsSecretKey();

    [GeneratedRegex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}\b")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{22,}\b")]
    private static partial Regex GitHubPat();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z\-_]{35}\b")]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"\bxox[baprs]-[0-9A-Za-z-]{10,48}\b")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"https://hooks\.slack\.com/services/[A-Za-z0-9/]+")]
    private static partial Regex SlackWebhook();

    [GeneratedRegex(@"\bsk_live_[0-9A-Za-z]{16,}\b")]
    private static partial Regex StripeSecretKey();

    [GeneratedRegex(@"\brk_live_[0-9A-Za-z]{16,}\b")]
    private static partial Regex StripeRestrictedKey();

    [GeneratedRegex(@"\bsk-proj-[A-Za-z0-9_-]{20,}\b")]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"\bnpm_[A-Za-z0-9]{36}\b")]
    private static partial Regex NpmToken();

    [GeneratedRegex(@"(?i)AccountKey=[A-Za-z0-9+/=]{40,}")]
    private static partial Regex AzureStorageKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)(?:password|passwd|pwd|secret|api[_-]?key|apikey|access[_-]?token|auth[_-]?token|client[_-]?secret|private[_-]?key|token)\s*[:=]\s*['""](?<val>[^'""\n]{8,})['""]")]
    private static partial Regex HardcodedSecret();
}

/// <summary>What the scanner puts on the wire.</summary>
/// <remarks>
/// snake_case, unlike <c>SecretsJsonContext</c> next door: the credential store returns bare
/// strings, but <c>SecretHit</c> is an object the renderer reads field by field, and
/// <c>domain.ts</c> declares <c>rule_name</c>.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(IReadOnlyList<SecretHit>))]
internal sealed partial class SecurityJsonContext : JsonSerializerContext;
