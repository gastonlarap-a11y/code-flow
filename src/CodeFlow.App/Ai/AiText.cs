using System.Globalization;
using System.Text;

namespace CodeFlow.Ai;

/// <summary>
/// Text handling shared by every engine's output path.
/// </summary>
internal static class AiText
{
    /// <summary>
    /// Removes ANSI escape sequences from captured output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A CLI writing to a pipe should not colour its output, but several do anyway. Left in, the
    /// escapes reach the activity log as mojibake, and worse, they break the engines that match on
    /// their own output: a colourised "Insufficient balance" no longer matches the quota signal and
    /// the user gets a raw error instead of the friendly banner.
    /// </para>
    /// <para>
    /// Two forms are handled, both by structure rather than by regex, so an unrecognised sequence
    /// is skipped rather than half-printed: CSI (<c>ESC [</c> … final byte in <c>@</c>–<c>~</c>)
    /// and OSC (<c>ESC ]</c> … terminated by BEL or <c>ESC \</c>). Any other two-byte escape drops
    /// both characters.
    /// </para>
    /// </remarks>
    public static string StripAnsi(string text)
    {
        if (!text.Contains('', StringComparison.Ordinal))
        {
            return text;
        }

        var output = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '')
            {
                output.Append(text[i]);
                continue;
            }

            if (++i >= text.Length)
            {
                break;
            }

            switch (text[i])
            {
                case '[':
                    while (++i < text.Length && text[i] is < '@' or > '~')
                    {
                        // Parameter and intermediate bytes; the final byte ends the sequence.
                    }

                    break;

                case ']':
                    while (++i < text.Length)
                    {
                        if (text[i] == '')
                        {
                            break;
                        }

                        if (text[i] == '')
                        {
                            i++;
                            break;
                        }
                    }

                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Removes a single outer <c>```</c> fence, for the operations whose answer is written to a file.
    /// </summary>
    /// <remarks>
    /// Some models wrap their answer in a fence despite being told not to, and the conflict resolver
    /// and inline edit both hand their result straight to a buffer or to disk. A fence opened and
    /// never closed leaves the body: only the opening line is dropped. Text with no fence at all is
    /// returned trimmed, unchanged otherwise.
    /// </remarks>
    public static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
        {
            // A fence and nothing else — there is no content to keep.
            return string.Empty;
        }

        var body = trimmed[(firstBreak + 1)..].TrimEnd();
        if (body.EndsWith("```", StringComparison.Ordinal))
        {
            body = body[..^3];
        }

        return body.TrimEnd();
    }

    /// <summary>
    /// Appends the "who answered this, on what, and when" footer to a generated analysis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stamped by the app rather than asked of the prompt: an engine cannot reliably know the wall
    /// clock or the model string it was launched with, and a model asked to fabricate a timestamp
    /// will. <paramref name="label"/> is the engine's display name.
    /// </para>
    /// <para>
    /// Spanish and emoji-keyed, verbatim — the footer travels with stored analyses that the renderer
    /// parses. <paramref name="when"/> is local time, as in 1.7.2; it is a parameter so a
    /// test can pin it.
    /// </para>
    /// </remarks>
    /// <param name="details">
    /// Extra segments appended to the same line, in order. Whatever the caller knows and this does
    /// not: how long the whole operation took, how much of the change reached the model, what the
    /// findings did since the last run. The separator is <c>·</c> throughout because the renderer
    /// splits on it to lay the line out as chips.
    /// </param>
    public static string StampFooter(
        string body,
        string kind,
        string label,
        string model,
        DateTimeOffset when,
        AiUsage? usage = null,
        IReadOnlyList<string>? details = null)
    {
        var modelLabel = string.IsNullOrWhiteSpace(model) ? "modelo predeterminado" : model;
        var timestamp = when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var extra = details is null ? string.Empty : string.Concat(details.Select(detail => $" · {detail}"));

        return $"{body}\n\n---\n"
            + $"🤖 Análisis automatizado ({kind}) · {label} ({modelLabel}) · {timestamp}{Spend(usage)}{extra}";
    }

    /// <summary>
    /// What the run consumed, appended to the stamp — or nothing at all when the engine did not say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stamp already answers "what produced this and when"; the missing half was always "at what
    /// price". Nothing recorded it, so comparing two runs meant reading the CLI's own session files
    /// by hand — which is how a review that got twice as fast was, for a while, only *believed* to
    /// have got cheaper.
    /// </para>
    /// <para>
    /// Cached reads are stated apart from the tokens billed at full price, because they are the
    /// number that moves most and costs least: summing them into one figure would make an agent that
    /// re-read the repository look identical to one that did not.
    /// </para>
    /// <para>
    /// <b>The money is labelled "equiv. API" because for most of this app's users it is not money.</b>
    /// The CLI reports <c>total_cost_usd</c> whatever the account is, computed from the token counts
    /// against the model's list price — so a Claude Pro or Max subscriber, who pays a flat fee and
    /// no per-token charge, is shown a figure they will never be billed. Printed bare it reads as an
    /// invoice. Kept, because it is still the quickest way to compare two runs, and labelled, because
    /// a number that means something else than it appears to is worse than no number. It is the
    /// engine's own arithmetic either way; nothing here multiplies tokens by a price list this
    /// codebase would then have to keep current.
    /// </para>
    /// </remarks>
    private static string Spend(AiUsage? usage)
    {
        if (usage is null)
        {
            return string.Empty;
        }

        var billed = usage.InputTokens + usage.OutputTokens + usage.CacheWriteTokens;
        var stamp = string.Create(
            CultureInfo.InvariantCulture,
            $" · {billed:N0} tokens ({usage.CacheReadTokens:N0} desde caché)");

        return usage.CostUsd is { } cost
            ? stamp + string.Create(CultureInfo.InvariantCulture, $" · equiv. API USD {cost:F4}")
            : stamp;
    }

    /// <summary>
    /// Pulls a version out of whatever a CLI's <c>--version</c> printed.
    /// </summary>
    /// <remarks>
    /// Every CLI answers differently — a bare number, a <c>v</c>-prefixed one, a banner with the
    /// product name around it. The first token that looks like a version wins; failing that, the
    /// whole first non-empty line, capped, because it ends up in a one-line stamp under a chat
    /// bubble rather than in a log.
    /// </remarks>
    public static string? ParseVersion(string output)
    {
        var line = output
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);

        if (line is null)
        {
            return null;
        }

        var token = line
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t =>
            {
                var core = t.TrimStart('v');
                return core.Contains('.', StringComparison.Ordinal) && core.Length > 0 && char.IsAsciiDigit(core[0]);
            }) ?? line;

        var value = new string([.. token.TrimStart('v').Trim().Take(40)]);
        return value.Length == 0 ? null : value;
    }
}
