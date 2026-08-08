using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeFlow.Review;

/// <summary>
/// The durable PR-review memory: turning a review's markdown into comparable findings, and diffing
/// a re-review against the run before it.
/// </summary>
/// <remarks>
/// <para>
/// Pure logic — no database, no network, no clock. The runs themselves live in <c>review_runs</c>
/// (<see cref="ReviewRunStore"/>) so everything travels inside <c>codeflow.db</c>.
/// </para>
/// <para>
/// The finding parse is deliberately minimal: it extracts only what memory and reconciliation need.
/// The canonical, user-facing render stays in <c>renderer/src/lib/parseAnalysis.ts</c>, and this has
/// to track the same header format — that is <c>XLANG-001</c>, a three-way contract between the
/// prompt that produces the markdown, this parser and the TypeScript one. The three patterns below
/// are copied character for character from 1.7.2; paraphrasing one makes reviews silently
/// parse to zero findings.
/// </para>
/// </remarks>
internal static partial class ReviewMemory
{
    /// <summary>
    /// A finding's severity, from the word the model wrote — falling back to the emoji.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The header carries the severity twice: as one of five words inside the brackets, and as one of
    /// three emoji the prompt asks be derived from it. Both parsers used to read only the emoji and
    /// throw the word away, so when the model wrote <c>### 🚨 [Mayor · Security Hotspot]</c> — the
    /// right word, the wrong emoji, against its own instructions — two <c>Mayor</c> findings were
    /// stored as <c>critical</c> and the Quality Gate went red for them. Observed on this
    /// repository's own pull request.
    /// </para>
    /// <para>
    /// The word wins because it is the one the model reasoned about; the emoji is decoration derived
    /// from it, one lossy step further away — three symbols for five levels. The emoji stays as the
    /// fallback for a word this does not recognise, which is exactly the behaviour that was there
    /// before, so nothing that parsed then stops parsing now.
    /// </para>
    /// <para>
    /// <c>XLANG-001</c>: <c>renderer/src/lib/parseAnalysis.ts</c> holds the same table and the two
    /// change together.
    /// </para>
    /// </remarks>
    internal static string SeverityOf(string severity, string emoji) =>
        severity.Trim().ToLowerInvariant() switch
        {
            "blocker" or "crítico" or "critico" => "critical",
            "mayor" => "warning",
            "menor" or "info" => "info",
            _ => emoji switch
            {
                "🚨" => "critical",
                "⚠️" => "warning",
                _ => "info",
            },
        };

    /// <summary>Parses the slim finding projection out of a review's markdown.</summary>
    /// <remarks>
    /// Every field is read from the finding's <em>own</em> block — the span from its header to the
    /// next one — so a <c>📍</c> or <c>🎯</c> line belonging to finding N+1 can never bleed into
    /// finding N.
    /// </remarks>
    public static List<MemoryFinding> ParseFindings(string reviewMarkdown)
    {
        var headers = HeaderPattern().Matches(reviewMarkdown);
        var findings = new List<MemoryFinding>(headers.Count);

        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var blockEnd = i + 1 < headers.Count ? headers[i + 1].Index : reviewMarkdown.Length;
            var block = reviewMarkdown[header.Index..blockEnd];

            var (archivo, lineas) = LocationPattern().Match(block) is { Success: true } location
                ? ParseLocation(location.Groups[1].Value)
                : (null, null);

            var confidence = ConfidencePattern().Match(block);

            findings.Add(new MemoryFinding
            {
                Id = header.Groups[5].Value.Trim(),
                Severity = SeverityOf(header.Groups[2].Value, header.Groups[1].Value),
                Tipo = header.Groups[3].Value.Trim(),
                Categoria = header.Groups[4].Value.Trim(),
                Subtitulo = Subtitle(block),
                Archivo = archivo,
                Lineas = lineas,
                Confianza = confidence.Success
                    && long.TryParse(confidence.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : null,
                // Everything below is fixed on a first parse. Reconcile fills in the real values.
                Estado = MemoryFinding.Open,
                IntroducidoEnIter = 0,
            });
        }

        return findings;
    }

    /// <summary>How deep each review level looks. Higher sees everything a lower one sees.</summary>
    /// <remarks>
    /// The three names are <c>XLANG</c> literals shared with <c>prStore.ts</c> and the level prompt
    /// files; anything else — including the empty string a pre-<c>DIVERGENCE-REVIEW-b</c> run stored
    /// — is unranked and never counts as deeper than the current run.
    /// </remarks>
    private static int Depth(string level) => level.ToLowerInvariant() switch
    {
        "basico" => 1,
        "completo" => 2,
        "ultra" => 3,
        _ => 0,
    };

    /// <summary>
    /// Whether this run looks less deeply than the run that found a given finding.
    /// </summary>
    /// <remarks>
    /// Both levels have to be known. An unranked level on either side answers false, so a history
    /// written before findings carried a level keeps behaving exactly as it did.
    /// </remarks>
    private static bool LooksShallower(string current, string found)
    {
        var (now, then) = (Depth(current), Depth(found));

        return now > 0 && then > 0 && now < then;
    }

    /// <summary>
    /// Rewrites the model's own <c>F-NNN</c> headers to the ids reconciliation actually assigned.
    /// </summary>
    /// <param name="reviewMarkdown">The model's text, exactly as it was written.</param>
    /// <param name="parsed">What <see cref="ParseFindings"/> read out of it, in header order.</param>
    /// <param name="reconciled">The merged set <see cref="Reconcile"/> returned.</param>
    /// <remarks>
    /// <para>
    /// <c>DIVERGENCE-REVIEW-a</c>, and the reason it is a divergence rather than a bug fix is worth
    /// stating. The model writes the whole review including its own numbering, restarting at
    /// <c>F-001</c> every run; <see cref="Reconcile"/> separately assigns each finding a stable id so
    /// a persisting finding can reuse its pull-request thread. Nothing reconciled those two, so the
    /// number a human read could name a different finding from the one the posting flow acted on.
    /// </para>
    /// <para>
    /// WF-PR-REVIEWER — the document these rules were ported from, and which
    /// <c>90-ambiguities.md</c> recorded as unavailable — cannot have this problem: its
    /// <c>report-standard.md</c> §3.1 has the model write a minimal JSON draft and an engine assign
    /// the ids and render the report, so there is one source of truth by construction. The drift
    /// arrived when the port kept the model's free-written markdown and dropped the render step. It
    /// was never a deliberate choice, which is what reading the document settled.
    /// </para>
    /// <para>
    /// <b>Only the header line is touched.</b> A paragraph mentioning "F-001" is the model's prose,
    /// not a reference this engine controls, and rewriting it would be editing the review's argument.
    /// </para>
    /// <para>
    /// The mapping is positional — <paramref name="parsed"/>[i] became
    /// <paramref name="reconciled"/>[i], because <see cref="Reconcile"/> appends exactly one entry per
    /// current finding before it appends anything else — but that is <em>checked</em> rather than
    /// assumed: every pair must still share an identity. If any does not, or the counts disagree, the
    /// text is returned untouched. A renumbering that guessed would be worse than the drift.
    /// </para>
    /// </remarks>
    public static string RenumberHeaders(
        string reviewMarkdown, IReadOnlyList<MemoryFinding> parsed, IReadOnlyList<MemoryFinding> reconciled)
    {
        if (parsed.Count == 0 || reconciled.Count < parsed.Count)
        {
            return reviewMarkdown;
        }

        for (var i = 0; i < parsed.Count; i++)
        {
            if (Identity(parsed[i]) != Identity(reconciled[i]))
            {
                return reviewMarkdown;
            }
        }

        var headers = HeaderPattern().Matches(reviewMarkdown);
        if (headers.Count != parsed.Count)
        {
            return reviewMarkdown;
        }

        var rewritten = new StringBuilder(reviewMarkdown.Length);
        var cursor = 0;

        for (var i = 0; i < headers.Count; i++)
        {
            // Group 5 is the F-NNN token itself, so only those characters move — the emoji, the
            // severity, the category and the spacing the frontend parses all stay byte-for-byte.
            var id = headers[i].Groups[5];

            rewritten.Append(reviewMarkdown, cursor, id.Index - cursor).Append(reconciled[i].Id);
            cursor = id.Index + id.Length;
        }

        return rewritten.Append(reviewMarkdown, cursor, reviewMarkdown.Length - cursor).ToString();
    }

    /// <summary>
    /// The identity a finding keeps across runs — file plus category, so it is still recognised
    /// once its line numbers have drifted.
    /// </summary>
    /// <remarks>
    /// Public because the posting flow keys on the same thing: it matches a comment the user picked
    /// back to its stored finding, to reuse that finding's thread. <c>BUG-REVIEW-b</c>: this is not
    /// injective, so two findings sharing a file and a category are indistinguishable here and both
    /// alias onto the same previous one. Reproduced, not fixed.
    /// </remarks>
    public static string FindingIdentity(string? archivo, string categoria) =>
        $"{(archivo ?? "").TrimStart('/').ToLowerInvariant()}|{categoria.ToLowerInvariant()}";

    /// <summary>
    /// Reconciles a fresh parse against the previous run, returning the merged finding set and the
    /// delta counts.
    /// </summary>
    /// <param name="previous">The stored findings of the run immediately before this one.</param>
    /// <param name="current">This run's fresh parse.</param>
    /// <param name="previousIter">The iteration number of <paramref name="previous"/>.</param>
    /// <param name="changedFiles">
    /// The files that changed since the previous run, when that is known. A previously active
    /// finding on a file that did <em>not</em> change auto-persists, because its code was never
    /// re-analysed and so cannot have been observed fixed. <see langword="null"/> means a full
    /// review, where any unmatched active finding is treated as resolved.
    /// </param>
    /// <remarks>
    /// Called only when the pull request already has a saved run; a first-ever review skips this
    /// entirely. Mirrors WF-PR-REVIEWER's <c>re-review.md</c>, per 1.7.2's own comment —
    /// a document that is not available, which is why the two <c>AMBIGUOUS-REVIEW-*</c> markers
    /// stay open rather than being reasoned away.
    /// </remarks>
    public static (List<MemoryFinding> Merged, ReviewDelta Delta) Reconcile(
        IReadOnlyList<MemoryFinding> previous,
        IReadOnlyList<MemoryFinding> current,
        int previousIter,
        IReadOnlyList<string>? changedFiles,
        string level = "")
    {
        var iterActual = previousIter + 1;
        var nextId = Math.Max(MaxIdNumber(previous), MaxIdNumber(current)) + 1;

        var merged = new List<MemoryFinding>(previous.Count + current.Count);

        // BUG-REVIEW-b, fixed after parity: which previous findings have already been claimed, by
        // position rather than by key. The identity `{file}|{category}` is not injective — one file
        // can hold two findings of the same category, which is ordinary rather than exotic — and
        // matching each current finding against `previous.FirstOrDefault(…)` gave both of them the
        // *same* previous row. They came out of the merge sharing an id and a thread id, so the
        // "stable id" was not stable and the posting flow wrote both findings into one thread.
        //
        // Tracked by index because MemoryFinding is a record: two genuinely distinct findings that
        // happen to carry equal field values are equal by value, so a set of the findings themselves
        // would collapse exactly the duplicates this exists to tell apart.
        var claimed = new bool[previous.Count];
        var (nuevos, persisten, resueltos, fueraDeAlcance) = (0, 0, 0, 0);

        foreach (var cur in current)
        {
            var key = Identity(cur);
            var matchIndex = ClaimPrevious(previous, claimed, key);
            var match = matchIndex >= 0 ? previous[matchIndex] : null;

            if (match is null || match.Estado == MemoryFinding.Resolved)
            {
                // Never seen, or seen and resolved and now back: either way a brand-new finding,
                // with a fresh id and iteration. The old thread is deliberately not carried over,
                // so a later post opens a new thread instead of reopening the closed one.
                merged.Add(cur with
                {
                    Id = FormattableString.Invariant($"F-{nextId:000}"),
                    Estado = MemoryFinding.Open,
                    IntroducidoEnIter = iterActual,
                    Delta = "nuevo",
                    Nivel = level,
                });
                nextId++;
                nuevos++;
                continue;
            }

            // Still present and previously seen, active or human-discarded → it persists, keeping
            // everything the previous run knew about it.
            var persisted = cur with
            {
                Id = match.Id,
                Estado = match.Estado,
                ThreadId = match.ThreadId,
                IntroducidoEnIter = match.IntroducidoEnIter == 0 ? Math.Max(previousIter, 1) : match.IntroducidoEnIter,
                MotivoDescarte = match.MotivoDescarte,
                Delta = "persiste",
                // This run saw it, so this run's depth is what a later one is compared against — not
                // the depth that first found it. A finding surfaced by `ultra` and then re-found by
                // `completo` is demonstrably visible at `completo`.
                Nivel = level,
            };

            if (persisted.IsActive)
            {
                persisten++;
            }

            // Claimed only here, on the persisting branch. The "new" branch above deliberately leaves
            // its match unclaimed: a resolved finding that reappeared is re-emitted below as its own
            // historical row, alongside the fresh one, and that traceability is the point.
            claimed[matchIndex] = true;
            merged.Add(persisted);
        }

        for (var i = 0; i < previous.Count; i++)
        {
            var prev = previous[i];

            // By index rather than by key, which is the other half of BUG-REVIEW-b: with two previous
            // findings sharing an identity, a key check here skipped *both* as soon as either one
            // matched, and the unmatched one silently vanished from the merged set instead of being
            // resolved or carried forward. The id check still stops a persisting one being emitted
            // twice.
            if (claimed[i] || merged.Any(m => m.Id == prev.Id))
            {
                continue;
            }

            if (!prev.IsActive)
            {
                // Already resolved or discarded: carried forward untouched, for traceability.
                merged.Add(prev with { Delta = "persiste" });
                continue;
            }

            var fileTouched = changedFiles is null || prev.Archivo is null
                || FileInChanged(prev.Archivo, changedFiles);

            if (fileTouched && LooksShallower(level, prev.Nivel))
            {
                // DIVERGENCE-REVIEW-b. The file was re-reviewed, but at a level that does not look
                // for what this finding is — `basico` after an `ultra` run. "Gone" and "not examined"
                // are different answers, and marking it resolved would tell the user something was
                // fixed that nobody touched.
                merged.Add(prev with { Delta = "fuera_de_alcance" });
                persisten++;
                fueraDeAlcance++;
            }
            else if (fileTouched)
            {
                // Its file was re-reviewed and the finding is gone → resolved. The thread is kept
                // so the posting flow can reply on it.
                merged.Add(prev with
                {
                    Estado = MemoryFinding.Resolved,
                    ResueltoEnIter = iterActual,
                    Delta = "resuelto",
                });
                resueltos++;
            }
            else
            {
                merged.Add(prev with { Delta = "persiste" });
                persisten++;
            }
        }

        return (merged, new ReviewDelta(previousIter, iterActual, nuevos, persisten, resueltos, fueraDeAlcance));
    }

    /// <summary>
    /// The findings still open that this run did not restate, named rather than counted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A re-review only quotes what it found this time. Everything still open in a file that has not
    /// moved is carried forward by <see cref="Reconcile"/> without the model ever seeing it again —
    /// which is right, and is what makes a re-review cheap. But the reader was left with a banner
    /// saying <c>2 persisten</c> and no way to learn which two: they were in neither the body nor the
    /// resolved history. Observed on this repository's own pull request, where the two were a race
    /// window and a misleading comment, and nothing on screen said so.
    /// </para>
    /// <para>
    /// Only the ones the body does not already carry. A finding the model did restate this run needs
    /// no second mention.
    /// </para>
    /// </remarks>
    /// <param name="restated">
    /// What this run's own text already covers. Matched by <see cref="FindingIdentity"/> rather than
    /// by position in <paramref name="findings"/>: <see cref="Reconcile"/> happens to emit the
    /// restated ones first, and a section that quietly depended on that would break the day the
    /// order changed, with nothing failing.
    /// </param>
    /// <returns><see langword="null"/> when every open finding is already in the body.</returns>
    public static string? PersistingSection(
        IReadOnlyList<MemoryFinding> findings, IReadOnlyList<MemoryFinding> restated)
    {
        var covered = restated
            .Select(f => FindingIdentity(f.Archivo, f.Categoria))
            .ToHashSet(StringComparer.Ordinal);

        var silent = findings
            .Where(f => f.Estado is MemoryFinding.Open or MemoryFinding.Posted)
            .Where(f => !covered.Contains(FindingIdentity(f.Archivo, f.Categoria)))
            .ToList();

        if (silent.Count == 0)
        {
            return null;
        }

        var section = new StringBuilder("\n\n---\n\n### 📌 Siguen abiertos de revisiones anteriores\n\n");

        foreach (var f in silent)
        {
            section.Append(CultureInfo.InvariantCulture,
                $"- `{f.Categoria}` · {f.Archivo ?? "—"} — {f.Id}, introducido iter {f.IntroducidoEnIter}"
                + $"; sin cambios en ese archivo desde entonces\n");
        }

        return section.ToString();
    }

    /// <summary>
    /// The cumulative traceability section appended to a re-review's body: every finding resolved
    /// or human-discarded over the pull request's life.
    /// </summary>
    /// <returns><see langword="null"/> when there is nothing to show, so a first review stays clean.</returns>
    public static string? ResolvedHistorySection(IReadOnlyList<MemoryFinding> findings)
    {
        var resolved = findings.Where(f => f.Estado == MemoryFinding.Resolved).ToList();
        var discarded = findings
            .Where(f => f.Estado is MemoryFinding.FalsePositive or MemoryFinding.Ignored)
            .ToList();

        if (resolved.Count == 0 && discarded.Count == 0)
        {
            return null;
        }

        var section = new StringBuilder();

        if (resolved.Count > 0)
        {
            section.Append("\n\n---\n\n### 🕘 Historial de hallazgos resueltos (trazabilidad)\n\n");
            foreach (var f in resolved)
            {
                section.Append(CultureInfo.InvariantCulture,
                    $"- `{f.Categoria}` · {f.Archivo ?? "—"} — introducido iter {f.IntroducidoEnIter} · resuelto iter {f.ResueltoEnIter ?? 0}\n");
            }
        }

        if (discarded.Count > 0)
        {
            section.Append("\n### 🗂️ Hallazgos descartados\n\n");
            foreach (var f in discarded)
            {
                var estado = f.Estado == MemoryFinding.FalsePositive ? "falso positivo" : "ignorado";
                var motivo = string.IsNullOrEmpty(f.MotivoDescarte) ? "" : $": {f.MotivoDescarte}";
                section.Append(CultureInfo.InvariantCulture,
                    $"- `{f.Categoria}` · {f.Archivo ?? "—"} — {estado}{motivo}\n");
            }
        }

        return section.ToString();
    }

    /// <summary>
    /// The one-line summary prepended to a re-review, so the user sees what changed before reading
    /// anything else.
    /// </summary>
    /// <remarks>
    /// It sits ahead of the findings, where the frontend parser leaves it out of the findings list
    /// and renders it as prose. The trailing blank line is part of the string because the banner is
    /// prepended straight onto the review body.
    /// </remarks>
    public static string DeltaBanner(ReviewDelta delta) => FormattableString.Invariant(
        $"🔁 Re-revisión (iter {delta.IterPrevia} → {delta.IterActual}): {delta.Nuevos} nuevos · {delta.Persisten} persisten · {delta.Resueltos} resueltos{OutOfScope(delta)}\n\n");

    /// <summary>The out-of-scope tail, present only when there is something to say.</summary>
    /// <remarks>
    /// <c>DIVERGENCE-REVIEW-b</c>. Appended rather than woven in, and omitted at zero, so a banner
    /// from a run where every file was examined at the same depth is byte-for-byte what it always
    /// was. Without it the count would read "3 persisten" while two of the three were never looked
    /// at — which is the misreading this whole change exists to prevent.
    /// </remarks>
    private static string OutOfScope(ReviewDelta delta) => delta.FueraDeAlcance == 0
        ? string.Empty
        : FormattableString.Invariant($" · {delta.FueraDeAlcance} fuera de alcance a este nivel");

    /// <summary>
    /// The first line of a block after its header that says something — skipping the structured
    /// fields, which have their own parsers.
    /// </summary>
    private static string Subtitle(string block)
    {
        var lines = block.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            // Ordinal, and against strings rather than chars: both markers are outside the BMP, so
            // each is a surrogate pair in UTF-16 where 1.7.2 compares one Unicode scalar.
            if (line.Length > 0
                && !line.StartsWith("📍", StringComparison.Ordinal)
                && !line.StartsWith("💭", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return "";
    }

    /// <summary>Splits a <c>📍 Ubicación</c> value into its file and its line range.</summary>
    /// <remarks>
    /// Strips the markdown wrapping the model sometimes adds, then splits on the <em>last</em>
    /// colon — the same tolerance the frontend's <c>parseLocation</c> has. The split only counts
    /// when there is a file on the left and at least one digit on the right, so a bare path, or a
    /// Windows drive letter with no line number after it, stays a file-only location.
    /// </remarks>
    private static (string? Archivo, string? Lineas) ParseLocation(string raw)
    {
        var cleaned = raw.Trim().Replace("`", "").Replace("*", "").Replace("_", "").Trim();
        var lastColon = cleaned.LastIndexOf(':');

        if (lastColon >= 0)
        {
            var file = cleaned[..lastColon];
            var lines = cleaned[(lastColon + 1)..];
            if (file.Trim().Length > 0 && lines.Any(char.IsAsciiDigit))
            {
                return (file.Trim(), lines.Trim());
            }
        }

        return (cleaned.Length > 0 ? cleaned : null, null);
    }

    /// <summary>
    /// The index of the previous finding a current one matches, or <c>-1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>BUG-REVIEW-b</c>.</b> Only unclaimed rows are eligible, so two current findings sharing
    /// an identity match two different previous rows rather than the same one twice.
    /// </para>
    /// <para>
    /// An <em>active</em> row is preferred over a resolved one carrying the same identity. Order
    /// alone would otherwise decide it, and picking the resolved row files a finding that has been
    /// open all along as brand new — losing its thread, its introduction iteration and any human
    /// decision recorded against it.
    /// </para>
    /// </remarks>
    private static int ClaimPrevious(IReadOnlyList<MemoryFinding> previous, bool[] claimed, string key)
    {
        var fallback = -1;

        for (var i = 0; i < previous.Count; i++)
        {
            if (claimed[i] || Identity(previous[i]) != key)
            {
                continue;
            }

            if (previous[i].Estado != MemoryFinding.Resolved)
            {
                return i;
            }

            fallback = fallback < 0 ? i : fallback;
        }

        return fallback;
    }

    /// <summary>The identity used for matching, with the fallback 1.7.2 applies.</summary>
    /// <remarks>
    /// Only a finding with neither a location nor even a category falls back to its subtitle. One
    /// with a category but no file keeps the file+category key as it stands.
    /// </remarks>
    private static string Identity(MemoryFinding finding)
    {
        var key = FindingIdentity(finding.Archivo, finding.Categoria);
        return key == "|" ? finding.Subtitulo.ToLowerInvariant() : key;
    }

    /// <summary>
    /// The highest <c>F-NNN</c> correlative in a set, so a fresh id cannot collide with one either
    /// side already used.
    /// </summary>
    private static int MaxIdNumber(IReadOnlyList<MemoryFinding> findings)
    {
        var max = 0;
        foreach (var finding in findings)
        {
            if (finding.Id.StartsWith("F-", StringComparison.Ordinal)
                && int.TryParse(finding.Id[2..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                max = Math.Max(max, n);
            }
        }

        return max;
    }

    /// <summary>Whether a finding's file is one of the files that changed.</summary>
    /// <remarks>
    /// The review markdown's path and git's own differ by a leading slash, or by one being more
    /// fully qualified, so either being a suffix of the other counts as a match.
    /// </remarks>
    private static bool FileInChanged(string findingFile, IReadOnlyList<string> changed)
    {
        var a = Normalise(findingFile);
        return changed.Select(Normalise).Any(c =>
            c == a || c.EndsWith(a, StringComparison.Ordinal) || a.EndsWith(c, StringComparison.Ordinal));

        static string Normalise(string path) => path.TrimStart('/').ToLowerInvariant();
    }

    [GeneratedRegex(@"(?m)^###\s*(🚨|⚠️|ℹ️)\s*\[([^·\]]+)·([^\]]+)\]\s*([^·]+)·\s*(F-\d+)\s*$")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"📍\s*Ubicaci[oó]n:\s*([^\n]+)")]
    private static partial Regex LocationPattern();

    [GeneratedRegex(@"🎯\s*Confianza:\s*(\d+)")]
    private static partial Regex ConfidencePattern();
}
