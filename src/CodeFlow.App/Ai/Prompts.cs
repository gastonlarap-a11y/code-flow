using System.Reflection;
using System.Text;

namespace CodeFlow.Ai;

/// <summary>
/// The built-in prompt texts, loaded verbatim from embedded resources.
/// </summary>
/// <remarks>
/// <para>
/// The instructions are English, like the rest of the codebase; <b>the labels they ask the model to
/// emit are not, and must never be translated</b>. <c>DEFAULT_PR_REVIEW_STANDARD</c> defines a
/// finding format that <em>two independent parsers</em> match on — one in the backend's review
/// memory, one in the renderer's <c>parseAnalysis.ts</c> — so <c>📍 Ubicación</c>,
/// <c>💭 Por qué</c>, <c>🎯 Confianza</c>, <c>📈 CALIDAD</c>, the severity words and the
/// <c>## NIVEL DE REVISIÓN ACTIVO:</c> header are payload, not prose. Rewriting one changes what
/// the model emits and breaks both parsers at once, and every stored <c>review_runs</c> row with
/// it. So is the standing order to answer in Spanish: the review is read in Spanish.
/// See <c>docs/business-rules/13-cross-language-contracts.md</c>, <c>XLANG-001</c>.
/// </para>
/// <para>
/// A workspace that already stored the Spanish methodology keeps it — <c>Settings</c> only falls
/// back to the built-in when the row is blank — so this reaches new workspaces and nobody's edited
/// copy.
/// </para>
/// <para>
/// Kept as embedded files rather than C# string literals so nothing passes through escaping.
/// Indentation is part of the payload: the PR-description template's leading whitespace is
/// significant, and a transcription into a source literal would silently add spaces to two thirds
/// of its lines.
/// </para>
/// </remarks>
public static class Prompts
{
    /// <summary>The PR review methodology. Seeded into every workspace by the storage migration.</summary>
    public static string DefaultPrReviewStandard { get; } = Load("DEFAULT_PR_REVIEW_STANDARD");

    /// <summary>The PR-description generator template.</summary>
    public static string DefaultPrDescriptionTemplate { get; } = Load("DEFAULT_PR_DESCRIPTION_TEMPLATE");

    /// <summary>The commit-message template.</summary>
    public static string DefaultCommitTemplate { get; } = Load("DEFAULT_COMMIT_TEMPLATE");

    /// <summary>The PR-review prompt.</summary>
    /// <remarks>
    /// Distinct from <see cref="DefaultPrReviewStandard"/>: this is the per-run instruction, that
    /// one is the workspace-editable methodology folded into it.
    /// </remarks>
    public static string DefaultReviewPrompt { get; } = Load("DEFAULT_REVIEW_PROMPT");

    /// <summary>The pre-commit "analyze changes" template.</summary>
    public static string DefaultAnalyzeTemplate { get; } = Load("DEFAULT_ANALYZE_TEMPLATE");

    /// <summary>
    /// The depth directive appended to a review prompt: how confident a finding must be, which
    /// severities survive, and how much to report.
    /// </summary>
    /// <remarks>
    /// Rides at the <em>end</em> of the prompt, after the methodology, so it overrides whatever
    /// depth that implies — the standard describes the method, the level tunes how strict it is.
    /// An unrecognised or empty level is <c>completo</c>, never an error, which is what makes the
    /// renderer's selector safe to extend without a backend change (<c>AI-022</c>).
    /// </remarks>
    /// <param name="explorable">
    /// Whether the run has a real checkout under it. Only <c>ultra</c> cares, and it is the one
    /// level that used to contradict itself: it told the model to read the whole method around every
    /// change while a link-only review's working directory held a description and a diff, and while
    /// the no-clone context in the same invocation told it not to try. The two instructions reached
    /// the model through different channels — one on argv, one on stdin — so neither read as wrong
    /// on its own.
    /// </param>
    internal static string ReviewLevelDirective(string level, bool explorable) => level switch
    {
        "basico" or "básico" => ReviewLevelBasico,
        "ultra" => explorable ? ReviewLevelUltra : ReviewLevelUltraNoClone,
        _ => ReviewLevelCompleto,
    };

    /// <summary>The merge-conflict resolution template.</summary>
    public static string DefaultResolveConflictTemplate { get; } = Load("DEFAULT_RESOLVE_CONFLICT_TEMPLATE");

    /// <summary>The system prompt framing an open-ended chat turn about the open repository.</summary>
    /// <remarks>
    /// Internal, and deliberately without a <c>default_*_template</c> command: 1.7.2 keeps
    /// this and <see cref="FixFindingSystemPrompt"/> private and appends them server-side only, so
    /// there is no user override to expose (<c>AI-053</c>). Sent on the first turn of a session,
    /// and on every turn for an engine that does not carry the conversation itself.
    /// </remarks>
    internal static string DefaultChatSystemPrompt { get; } = Load("DEFAULT_CHAT_SYSTEM_PROMPT");

    /// <summary>The system prompt for applying one review finding to the working tree.</summary>
    internal static string FixFindingSystemPrompt { get; } = Load("FIX_FINDING_SYSTEM_PROMPT");

    /// <summary>The system prompt for the editor's inline edit over a selection.</summary>
    /// <remarks>
    /// <c>pub</c> in 1.7.2 but reached by no command and overridable by no setting, so it is
    /// internal here too — <c>inline_edit</c> always sends exactly this text.
    /// </remarks>
    internal static string DefaultInlineEditPrompt { get; } = Load("DEFAULT_INLINE_EDIT_PROMPT");

    private static string ReviewLevelBasico { get; } = Load("REVIEW_LEVEL_BASICO");

    private static string ReviewLevelCompleto { get; } = Load("REVIEW_LEVEL_COMPLETO");

    private static string ReviewLevelUltra { get; } = Load("REVIEW_LEVEL_ULTRA");

    private static string ReviewLevelUltraNoClone { get; } = Load("REVIEW_LEVEL_ULTRA_NO_CLONE");

    private static string Load(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = $"CodeFlow.Ai.Prompts.{name}.txt";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"the prompt resource '{resource}' is missing — check the EmbeddedResource glob in the csproj");

        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }
}
