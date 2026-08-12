using System.Globalization;
using System.Text;

namespace CodeFlow.Ai;

/// <summary>
/// The provider-neutral AI operations.
/// </summary>
/// <remarks>
/// <para>
/// Each one assembles a prompt and a stdin payload, runs it through
/// <see cref="AiEngineRunner"/>, and shapes the answer. They are neutral by construction: switching
/// Claude ⇆ Gemini ⇆ Ollama is a settings change, so nothing here may branch on the provider.
/// </para>
/// <para>
/// The Spanish strings are verbatim and stay Spanish — see the exemption documented on
/// <see cref="Prompts"/>. The user-facing errors are part of it: the frontend shows them as-is.
/// </para>
/// </remarks>
internal static class AiOperations
{
    /// <summary>Cap on a commit-message diff and on an inline edit's file context.</summary>
    private const int MaxDiffChars = 20_000;

    /// <summary>Cap on each of the three sides handed to the conflict resolver.</summary>
    private const int MaxConflictSideChars = 40_000;

    /// <summary>Cap on a single project or review context.</summary>
    /// <remarks>
    /// Per context rather than over all of them together: the contexts a workspace enables are each
    /// meant to say one thing, and a shared pool would let the first one starve the rest — the same
    /// failure `GIT-031` fixed for files. Generous, because a context is prose the user chose to
    /// include; the point is a ceiling, not a diet.
    /// </remarks>
    private const int MaxContextChars = 30_000;

    /// <summary>Drafts a commit message from a staged diff.</summary>
    /// <remarks>
    /// No footer: this text goes into the commit-message box, where a stamp would end up in the
    /// repository's history. <paramref name="model"/> is resolved by the caller — the per-task commit
    /// override, or the engine's fast model when the user has not set one.
    /// <para>
    /// The reply goes through <see cref="AiText.StripCodeFence"/> like the other two whose output is
    /// meant to be used verbatim (<c>AI-018</c>): the template now allows a summary plus a bulleted
    /// body, and a multi-line answer is exactly what a model tends to wrap in a fence despite being
    /// told not to — which would then be committed literally, backticks and all.
    /// </para>
    /// </remarks>
    public static async Task<string> GenerateCommitMessageAsync(
        AiRunner runner,
        AiConfig config,
        string diff,
        string promptTemplate,
        AiRunContext? run,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            throw new AiRunFailedException("No staged changes to summarize");
        }

        var invocation = new AiInvocation(
            Prompt: Fallback(promptTemplate, Prompts.DefaultCommitTemplate),
            StdinContent: Cap(diff, MaxDiffChars),
            Model: config.Model);

        var result = await runner(config, invocation, run, cancellationToken).ConfigureAwait(false);
        return AiText.StripCodeFence(result.Text);
    }

    /// <summary>Proposes a merged version of a conflicted file from its three index stages.</summary>
    /// <remarks>
    /// Returns the full file content, conflict markers and any wrapping code fence stripped. Nothing
    /// is written here: the frontend shows it for review and only writes and stages it once the user
    /// accepts.
    /// </remarks>
    public static async Task<string> ResolveConflictAsync(
        AiRunner runner,
        AiConfig config,
        string relativePath,
        string @base,
        string ours,
        string theirs,
        string promptTemplate,
        AiRunContext? run,
        CancellationToken cancellationToken)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             ARCHIVO: {relativePath}

             === BASE (ancestro común) ===
             {Cap(@base, MaxConflictSideChars)}

             === OURS (rama actual) ===
             {Cap(ours, MaxConflictSideChars)}

             === THEIRS (rama entrante) ===
             {Cap(theirs, MaxConflictSideChars)}
             """);

        var invocation = new AiInvocation(
            Prompt: Fallback(promptTemplate, Prompts.DefaultResolveConflictTemplate),
            StdinContent: payload,
            Model: config.Model);

        var result = await runner(config, invocation, run, cancellationToken).ConfigureAwait(false);
        return AiText.StripCodeFence(result.Text);
    }

    /// <summary>Reviews a pull request's diff and returns the review as markdown.</summary>
    /// <remarks>
    /// <para>
    /// Nothing is posted to the pull-request host here — publishing findings is a separate, explicit
    /// step the user takes after reading the review.
    /// </para>
    /// <para>
    /// The returned markdown is a parsed contract, not free prose: <c>ReviewMemory</c> and the
    /// renderer's <c>parseAnalysis.ts</c> both match on the finding headers the prompt asks for
    /// (<c>XLANG-001</c>). That is why the footer is stamped with <c>pr-review</c> and why the
    /// template falls back to the built-in rather than to nothing.
    /// </para>
    /// </remarks>
    /// <param name="level">
    /// <c>basico</c> · <c>completo</c> · <c>ultra</c>. Anything else is <c>completo</c>.
    /// </param>
    /// <param name="codeContext">
    /// The declaration around each change, already extracted (<see cref="Git.ChangeContext"/>), or
    /// empty when there is no working tree to extract it from. It rides <em>after</em> the diff: the
    /// diff is what the review is of, and this is the code it lands in.
    /// </param>
    /// <param name="explorable">
    /// Whether the working directory is a real checkout. False for a review reached by link, whose
    /// directory holds two files — and where a directive telling the model to go read the
    /// surrounding code would be asking for something it cannot do.
    /// </param>
    /// <returns>
    /// The run itself, unstamped. The footer is the review pipeline's to write, because half of what
    /// belongs in it — how long the whole thing took, what the diff left out, what changed since the
    /// last review — is known there and not here.
    /// </returns>
    public static async Task<AiRun> ReviewPullRequestAsync(
        AiRunner runner,
        AiConfig config,
        string prTitle,
        string prDescription,
        IReadOnlyList<(string Name, string Content)> contexts,
        string diffText,
        string codeContext,
        string workingDirectory,
        string promptTemplate,
        string level,
        bool explorable,
        string? mcpConfigPath,
        AiRunContext? run,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diffText))
        {
            throw new AiRunFailedException("This pull request has no changes to review");
        }

        var payload = new StringBuilder();
        payload.Append("PR TITLE: ").Append(prTitle).Append("\n\nPR DESCRIPTION:\n")
            .Append(string.IsNullOrWhiteSpace(prDescription) ? "(no description)" : prDescription)
            .Append("\n\n");
        AppendContexts(payload, contexts, heading: "PROJECT REVIEW CONTEXT:");

        // Appended as it arrives: `Diff.RenderForPrompt` already spent its budget deliberately,
        // sharing it between files and naming whatever it left out. Cutting it again here by
        // character count would silently undo that — which is the defect this replaced.
        payload.Append("DIFF:\n").Append(diffText);

        if (!string.IsNullOrEmpty(codeContext))
        {
            payload.Append('\n').Append(codeContext);
        }

        var invocation = new AiInvocation(
            Prompt: $"{Fallback(promptTemplate, Prompts.DefaultReviewPrompt)}\n\n"
                + Prompts.ReviewLevelDirective(level, explorable),
            StdinContent: payload.ToString(),
            Model: config.Model,
            AllowedTools: config.AllowedTools,
            Cwd: workingDirectory,
            McpConfigPath: mcpConfigPath);

        return await runner(config, invocation, run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks "there was nothing uncommitted to analyse" for the renderer, which only ever sees the
    /// message string.
    /// </summary>
    /// <remarks>
    /// The same device as <c>CREDENTIAL_REFUSED: </c> and <c>STALE_REVIEW: </c>: the transport
    /// carries a string and nothing else, and this is a state rather than a failure. The renderer
    /// shows an empty state instead of a red banner, and <see cref="AiTurn"/> files no history row —
    /// the analyze tab starts a run when it is merely *opened*, so a refusal here is not something
    /// the user asked for. <c>XLANG-015</c>, <c>AI-024</c>.
    ///
    /// The Spanish sentence behind the prefix is the message <c>AI-024</c> already documented; only
    /// the marker is new.
    /// </remarks>
    public const string NothingToAnalyzePrefix = "NOTHING_TO_ANALYZE: ";

    /// <summary>Scans the working tree's uncommitted diff for bugs before the user commits it.</summary>
    /// <remarks>
    /// Same shape as a PR review but pointed at the local diff, and it folds in the same
    /// workspace-level contexts. The footer is stamped because this text is stored and re-read long
    /// after the run.
    /// </remarks>
    public static async Task<string> AnalyzeChangesAsync(
        AiRunner runner,
        AiConfig config,
        IReadOnlyList<(string Name, string Content)> contexts,
        string diffText,
        ReviewScope scope,
        string workingDirectory,
        string promptTemplate,
        string? mcpConfigPath,
        AiRunContext? run,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diffText))
        {
            throw new AiRunFailedException(NothingToAnalyzePrefix + NothingFor(scope));
        }

        var payload = new StringBuilder();
        AppendContexts(payload, contexts);

        // The scope is a labelled line rather than something the prompt states, because
        // `analyze_template` is a user-editable setting: its built-in text says "UNCOMMITTED
        // changes", and anyone who had edited theirs would be describing the wrong diff.
        payload.Append("SCOPE: ").Append(ReviewScopes.Describe(scope)).Append("\n\n");

        // Already budgeted by `Diff.RenderForPrompt`; see the note in `ReviewPullRequestAsync`.
        payload.Append("DIFF:\n").Append(diffText);

        var invocation = new AiInvocation(
            Prompt: Fallback(promptTemplate, Prompts.DefaultAnalyzeTemplate),
            StdinContent: payload.ToString(),
            Model: config.Model,
            AllowedTools: config.AllowedTools,
            Cwd: workingDirectory,
            McpConfigPath: mcpConfigPath);

        var result = await runner(config, invocation, run, cancellationToken).ConfigureAwait(false);

        // What the CLI reports it actually ran beats what was configured: they differ whenever the
        // model setting is blank and the CLI picked its own default.
        return AiText.StampFooter(
            result.Text, "análisis pre-commit", config.Engine.Label, result.Model ?? config.Model, now,
            result.Usage);
    }

    /// <summary>
    /// Cap on the ticket's own prose — its description and the user's notes on it.
    /// </summary>
    /// <remarks>
    /// A budget of its own, because the alternative is the one this replaced: the diff spent 250 000
    /// characters through <c>PromptDiff</c> and the ticket was concatenated after it with no ceiling
    /// at all. A work item with a long refinement thread and four screenshots is not unusual, and
    /// under one shared pool the branch's own contribution is what would have starved.
    /// <para>
    /// The acceptance criteria are deliberately <b>not</b> capped by anything: they are what the
    /// change is being judged against, and truncating them turns "the model did not check AC-7" into
    /// a finding about the work rather than about the prompt.
    /// </para>
    /// </remarks>
    private const int MaxTicketChars = 40_000;

    /// <summary>Cap on the notes the user keeps beside the ticket.</summary>
    private const int MaxTicketNotesChars = 20_000;

    /// <summary>
    /// Reviews a branch's whole contribution against the work item it was written for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sibling of <see cref="AnalyzeChangesAsync"/> rather than a parameter on it. Two reasons, and
    /// both are about honesty of shape: that operation has no slot for a ticket, and passing the
    /// ticket through <paramref name="contexts"/> would render it under <c>PROJECT CONTEXT:</c> —
    /// a heading that means "standing rules of this project", which a work item is not.
    /// </para>
    /// <para>
    /// The answer carries two contracts at once: the finding blocks <c>ReviewMemory</c> and
    /// <c>parseAnalysis.ts</c> read (<c>XLANG-001</c>), and the criteria table
    /// <c>Tickets.TicketVerdict</c> reads (<c>XLANG-016</c>). They are disjoint slices of one text.
    /// </para>
    /// </remarks>
    /// <param name="criteria">
    /// The acceptance criteria as Markdown, already numbered when the ticket carried a list. Emitted
    /// uncapped; see <see cref="MaxTicketChars"/>.
    /// </param>
    /// <param name="criteriaMode"><c>list</c>, <c>prose</c> or <c>none</c> — <c>WI-007</c>.</param>
    /// <param name="notes">Whatever the user wrote in the mirror's <c>notes/</c> directory.</param>
    /// <returns>The run itself, unstamped: the footer is written once the verdict has been parsed.</returns>
    public static async Task<AiRun> ReviewBranchAgainstTicketAsync(
        AiRunner runner,
        AiConfig config,
        string ticketHeader,
        string ticketBody,
        string criteria,
        string criteriaMode,
        string notes,
        string branch,
        string baseRef,
        ReviewScope scope,
        IReadOnlyList<(string Name, string Content)> contexts,
        string diffText,
        string codeContext,
        string workingDirectory,
        string promptTemplate,
        string level,
        string? mcpConfigPath,
        AiRunContext? run,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diffText))
        {
            throw new AiRunFailedException(NothingToAnalyzePrefix + NothingFor(scope));
        }

        var payload = new StringBuilder();
        payload.Append("TICKET: ").Append(ticketHeader).Append("\n\nTICKET DESCRIPTION:\n")
            .Append(string.IsNullOrWhiteSpace(ticketBody) ? "(sin descripción)" : Cap(ticketBody, MaxTicketChars))
            .Append("\n\nCRITERIA MODE: ").Append(criteriaMode)
            .Append("\n\nACCEPTANCE CRITERIA:\n")
            .Append(string.IsNullOrWhiteSpace(criteria) ? "(el ticket no declara criterios)" : criteria)
            .Append("\n\n");

        if (!string.IsNullOrWhiteSpace(notes))
        {
            payload.Append("USER NOTES ON THIS TICKET:\n").Append(Cap(notes, MaxTicketNotesChars)).Append("\n\n");
        }

        AppendContexts(payload, contexts, heading: "PROJECT REVIEW CONTEXT:");

        payload.Append("BRANCH: ").Append(branch);
        if (scope is ReviewScope.Branch)
        {
            payload.Append("\nBASE: ").Append(baseRef);
        }

        // The caveat rides with the scope: judging only the uncommitted diff against acceptance
        // criteria hides the evidence for everything already committed, and without this the model
        // reports met criteria as unmet — systematically, which is worse than any false positive.
        payload.Append("\nSCOPE: ").Append(ReviewScopes.Describe(scope))
            .Append(ReviewScopes.CriteriaCaveat(scope))
            .Append("\n\n");

        // Already budgeted by `Diff.RenderForPrompt`; see the note in `ReviewPullRequestAsync`.
        payload.Append("DIFF:\n").Append(diffText);

        if (!string.IsNullOrEmpty(codeContext))
        {
            payload.Append('\n').Append(codeContext);
        }

        var invocation = new AiInvocation(
            Prompt: $"{Fallback(promptTemplate, Prompts.DefaultTicketReviewStandard)}\n\n"
                + Prompts.ReviewLevelDirective(level, explorable: true),
            StdinContent: payload.ToString(),
            Model: config.Model,
            AllowedTools: config.AllowedTools,
            Cwd: workingDirectory,
            McpConfigPath: mcpConfigPath);

        return await runner(config, invocation, run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drafts a pull-request description from the diff between two branches.</summary>
    /// <remarks>
    /// Returns the model's raw text — a <c>TITLE:</c> line followed by a markdown body. Splitting it is
    /// the command layer's job, as in 1.7.2, because the split is a wire-shape concern rather
    /// than part of the invocation. No footer is stamped: this text goes straight into a PR field, where
    /// a "generated by" line would be published to the repository.
    /// </remarks>
    public static async Task<string> GeneratePrDescriptionAsync(
        AiRunner runner,
        AiConfig config,
        string sourceBranch,
        string targetBranch,
        string diffText,
        string promptTemplate,
        AiRunContext? run,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diffText))
        {
            throw new AiRunFailedException("No hay diferencias entre las ramas para describir");
        }

        var payload = string.Create(
            CultureInfo.InvariantCulture,
            // Already budgeted by `Diff.RenderForPrompt`; see the note in `ReviewPullRequestAsync`.
            $"RAMA ORIGEN: {sourceBranch}\nRAMA DESTINO: {targetBranch}\n\nDIFF:\n{diffText}");

        var invocation = new AiInvocation(
            Prompt: Fallback(promptTemplate, Prompts.DefaultPrDescriptionTemplate),
            StdinContent: payload,
            Model: config.Model);

        var result = await runner(config, invocation, run, cancellationToken).ConfigureAwait(false);
        return result.Text;
    }

    /// <summary>One turn of an open-ended chat about the open repository.</summary>
    /// <remarks>
    /// Unlike the one-shot operations this resumes the engine's own session across turns, so the
    /// project context and system prompt are established once rather than re-explained every message.
    /// An engine that carries no session server-side (Ollama) has nothing to carry them forward, so
    /// it gets them every turn.
    /// </remarks>
    public static Task<AiRun> ChatAsync(
        AiRunner runner,
        AiConfig config,
        IReadOnlyList<(string Name, string Content)> contexts,
        string message,
        string? sessionId,
        string workingDirectory,
        string? mcpConfigPath,
        AiRunContext? run,
        CancellationToken cancellationToken)
    {
        var needsContext = sessionId is null || !config.Engine.ResumesSessions;

        var payload = new StringBuilder();
        if (needsContext)
        {
            AppendContexts(payload, contexts, trailingBlankLine: false);
        }

        var invocation = new AiInvocation(
            // stdin is the one-time context; the prompt carries the user's actual message.
            Prompt: message,
            StdinContent: payload.ToString(),
            SystemPrompt: needsContext ? Prompts.DefaultChatSystemPrompt : null,
            Model: config.Model,
            AllowedTools: config.AllowedTools,
            Cwd: workingDirectory,
            McpConfigPath: mcpConfigPath,
            ResumeSessionId: sessionId,
            // The chat is meant to help work on the repo, so it may create and edit files without an
            // unanswerable, headless permission prompt. Running commands still needs the shell tool
            // enabled in Settings.
            AutoApproveEdits: true);

        return runner(config, invocation, run, cancellationToken);
    }

    /// <summary>Applies one review finding's fix directly to the working tree.</summary>
    /// <remarks>
    /// The only write-capable operation, so it always runs with the engine's fixed write tool set
    /// rather than the user's general allow-list: clicking "fix with AI" is itself the opt-in, and
    /// there is no second setting to get wrong.
    /// </remarks>
    public static async Task<string> ApplyFindingFixAsync(
        AiRunner runner,
        AiConfig config,
        string findingPrompt,
        string workingDirectory,
        AiRunContext? run,
        CancellationToken cancellationToken)
    {
        // Defensive: the UI hides this for non-agentic providers, but never let a local model
        // silently "fix" nothing — it has no write tools — if the command is reached another way.
        if (!config.Engine.Agentic)
        {
            throw new AiRunFailedException(
                "Este proveedor local no puede aplicar cambios automáticamente. Usa Claude, Gemini u Open Code para \"Corregir con IA\".");
        }

        var invocation = new AiInvocation(
            Prompt: "Aplica la corrección para el hallazgo entregado por stdin.",
            StdinContent: findingPrompt,
            SystemPrompt: Prompts.FixFindingSystemPrompt,
            Model: config.Model,
            AllowedTools: config.Engine.FixTools,
            Cwd: workingDirectory,
            AutoApproveEdits: true);

        var result = await runner(config, invocation, run, cancellationToken).ConfigureAwait(false);
        return result.Text;
    }

    /// <summary>Rewrites a selected fragment of a file according to an instruction.</summary>
    /// <remarks>
    /// Text in, text out, deliberately: no tools and no file writes, so it works with every provider
    /// — a local Ollama model included — and the result lands in the editor's buffer as a normal,
    /// undoable edit rather than as a change made behind the user's back.
    /// </remarks>
    public static async Task<string> InlineEditAsync(
        AiRunner runner,
        AiConfig config,
        string relativePath,
        string fileContent,
        string selection,
        string instruction,
        AiRunContext? run,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            throw new AiRunFailedException("No hay código seleccionado para editar");
        }

        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             ARCHIVO: {relativePath}

             === CONTENIDO DEL ARCHIVO (contexto) ===
             {Cap(fileContent, MaxDiffChars)}

             === FRAGMENTO SELECCIONADO ===
             {selection}

             === INSTRUCCIÓN ===
             {instruction}
             """);

        var invocation = new AiInvocation(
            Prompt: "Reescribe el fragmento seleccionado según la instrucción.",
            StdinContent: payload,
            SystemPrompt: Prompts.DefaultInlineEditPrompt,
            Model: config.Model);

        var result = await runner(config, invocation, run, cancellationToken).ConfigureAwait(false);
        return AiText.StripCodeFence(result.Text);
    }

    /// <summary>
    /// Prefixes the enabled workspace contexts to a payload, or nothing when there are none.
    /// </summary>
    /// <remarks>
    /// One line per context under a heading. The analysis path adds a blank line after the block
    /// and the chat path does not, and the review path titles its block <c>PROJECT REVIEW
    /// CONTEXT:</c> where the others say <c>PROJECT CONTEXT:</c> — differences in 1.7.2
    /// that are kept, because each text reaches a model that was tuned against it.
    /// </remarks>
    private static void AppendContexts(
        StringBuilder payload,
        IReadOnlyList<(string Name, string Content)> contexts,
        bool trailingBlankLine = true,
        string heading = "PROJECT CONTEXT:")
    {
        if (contexts.Count == 0)
        {
            return;
        }

        payload.Append(heading).Append('\n');
        foreach (var (name, content) in contexts)
        {
            payload.Append("- ").Append(name).Append(": ");

            // Every context is a free-text field the user pastes into, stored in a SQLite `TEXT`
            // column that neither validates nor truncates. Pasting an architecture document into
            // one is a reasonable thing to do and used to enter the prompt whole, unbounded and
            // unannounced — the one payload here with no limit, while a commit diff has 20 000 and
            // a conflict side 40 000. The cut is named where it happens, for the same reason
            // `GIT-031` names its own: a model cannot allow for what it does not know is missing.
            if (content.Length <= MaxContextChars)
            {
                payload.Append(content).Append('\n');
                continue;
            }

            payload.Append(Cap(content, MaxContextChars))
                .Append(string.Create(
                    CultureInfo.InvariantCulture,
                    $"\n  [context truncated: {content.Length - MaxContextChars} more characters]\n"));
        }

        if (trailingBlankLine)
        {
            payload.Append('\n');
        }
    }

    /// <summary>What "there is nothing to review" means for each scope.</summary>
    /// <remarks>
    /// Two different facts, and telling them apart matters: a clean working tree is the ordinary
    /// state of a repository between edits, while a branch that contributes nothing over its base
    /// usually means the wrong base was chosen.
    /// </remarks>
    private static string NothingFor(ReviewScope scope) => scope switch
    {
        ReviewScope.Branch => "Esta rama no aporta ningún cambio sobre su rama base",
        _ => "No hay cambios sin commitear para analizar",
    };

    /// <summary>A stored template, or the built-in when the user has not set one.</summary>
    /// <remarks>Blank counts as unset, which is how "restore default" works: saving an empty string.</remarks>
    private static string Fallback(string template, string builtin) =>
        string.IsNullOrWhiteSpace(template) ? builtin : template;

    /// <summary>Truncates to a number of Unicode scalars, as 1.7.2's <c>chars().take()</c> does.</summary>
    private static string Cap(string text, int maxScalars)
    {
        if (text.Length <= maxScalars)
        {
            return text;
        }

        var index = 0;
        for (var scalars = 0; scalars < maxScalars && index < text.Length; scalars++)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
        }

        return text[..index];
    }
}

/// <summary>
/// How an operation reaches an engine: the seam the tests replace with a scripted engine.
/// </summary>
/// <remarks>
/// A delegate rather than an interface: there is exactly one production implementation
/// (<see cref="AiEngineRunner.RunAsync"/>, bound to its registry and HTTP client) and one test
/// double, which is not the two-real-implementations bar <c>.claude/rules/dotnet.md</c> sets for an
/// interface.
/// </remarks>
internal delegate Task<AiRun> AiRunner(
    AiConfig config, AiInvocation invocation, AiRunContext? run, CancellationToken cancellationToken);
