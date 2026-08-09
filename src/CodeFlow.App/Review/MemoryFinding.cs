using System.Text.Json.Serialization;

namespace CodeFlow.Review;

/// <summary>
/// One finding as remembered for reconciliation — a slim projection of a review's markdown, not the
/// full rendered finding.
/// </summary>
/// <remarks>
/// <para>
/// Persisted as the <c>review_runs.findings</c> JSON and carried across a pull request's runs, so a
/// finding's state (posted? resolved? marked?) and its comment thread survive a re-review. Nothing
/// in this application ever deletes one.
/// </para>
/// <para>
/// The property names are the contract, not an implementation detail: <c>renderer/src/types/domain.ts</c>
/// declares <c>SavedFinding</c> field for field against this record (<c>XLANG-010</c>), and the wire
/// policy is snake_case. Renaming a member here silently breaks the memory manager's rendering —
/// hence the Spanish names, which are what the stored data and the UI both use.
/// </para>
/// </remarks>
public sealed record MemoryFinding
{
    /// <summary>Lifecycle state of a finding nobody has posted or judged yet.</summary>
    public const string Open = "abierto";

    /// <summary>It has a comment thread on the pull request and is still present.</summary>
    public const string Posted = "posteado";

    /// <summary>It is no longer in the code.</summary>
    public const string Resolved = "resuelto";

    /// <summary>A human judged it wrong.</summary>
    public const string FalsePositive = "falso_positivo";

    /// <summary>A human judged it not worth acting on.</summary>
    public const string Ignored = "ignorado";

    /// <summary>The <c>F-NNN</c> correlative. On a first parse it is whatever the model wrote.</summary>
    /// <remarks>
    /// <c>DIVERGENCE-REVIEW-a</c>: a re-review assigns a stable id here, and
    /// <see cref="ReviewMemory.RenumberHeaders"/> rewrites the <c>F-NNN</c> the model printed so the
    /// two cannot drift apart. CodeFlow 1.7.2 does not do that — its markdown keeps the model's own
    /// numbering while thread reuse keys on this — and the source runbook's report standard shows
    /// why that was never intended: there, an engine assigns the id and renders the
    /// report, so a single source of truth is structural.
    /// </remarks>
    public string Id { get; init; } = "";

    /// <summary><c>critical</c> · <c>warning</c> · <c>info</c>, derived from the finding's emoji.</summary>
    public string Severity { get; init; } = "";

    public string Tipo { get; init; } = "";

    public string Categoria { get; init; } = "";

    public string Subtitulo { get; init; } = "";

    public string? Archivo { get; init; }

    public string? Lineas { get; init; }

    public long? Confianza { get; init; }

    /// <summary>Lifecycle state, carried across runs. One of the five constants on this type.</summary>
    public string Estado { get; init; } = Open;

    /// <summary>The pull-request comment thread this finding was posted to, if any.</summary>
    /// <remarks>
    /// Kept even once the finding is resolved, so a re-post replies to the same thread instead of
    /// opening a duplicate.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ThreadId { get; init; }

    /// <summary>The run number this finding was first seen in. Powers "introducido iter N".</summary>
    /// <remarks>
    /// <c>0</c> is a sentinel meaning "not assigned yet": that is what a fresh parse produces, and
    /// either <see cref="ReviewMemory.Reconcile"/> or the first-run path fills in a real number
    /// before the finding is stored.
    /// </remarks>
    public int IntroducidoEnIter { get; init; }

    /// <summary>The run it was detected as resolved in. Only set while <see cref="Estado"/> is <see cref="Resolved"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ResueltoEnIter { get; init; }

    /// <summary>Why a human marked it <see cref="FalsePositive"/> or <see cref="Ignored"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MotivoDescarte { get; init; }

    /// <summary>
    /// Only set on a re-review: <c>nuevo</c> · <c>persiste</c> · <c>resuelto</c> ·
    /// <c>fuera_de_alcance</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Delta { get; init; }

    /// <summary>
    /// The review level that found this — <c>basico</c>, <c>completo</c> or <c>ultra</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DIVERGENCE-REVIEW-b</c>. The source runbook records the level once per run, not per
    /// finding. Without it, a re-review at a shallower level marks
    /// everything the shallower level does not look for as <see cref="Resolved"/> — it cannot tell
    /// "fixed" from "not examined". Its report standard says persistence
    /// "always happens, at all three levels", but that is an instruction to the agent doing the
    /// judging, not a mechanism, so nothing enforced it. This is the mechanism, and it is a product
    /// decision the operator made.
    /// </para>
    /// <para>
    /// Empty means a run stored before this field existed. Such a finding is never treated as
    /// deeper than the current level, so existing histories cannot change meaning retroactively.
    /// </para>
    /// </remarks>
    public string Nivel { get; init; } = "";

    /// <summary>Whether this finding counts toward the severity buckets and the Quality Gate.</summary>
    /// <remarks>
    /// Resolved and human-discarded findings are carried for traceability but excluded from the
    /// active view.
    /// </remarks>
    [JsonIgnore]
    public bool IsActive => Estado is Open or Posted;
}

/// <summary>What a re-review changed, relative to the run immediately before it.</summary>
/// <param name="FueraDeAlcance">
/// Findings this run neither confirmed nor cleared, because it looked less deeply than the run that
/// found them (<c>DIVERGENCE-REVIEW-b</c>). They are counted in <paramref name="Persisten"/> as well —
/// they did persist — and reported separately so "3 persisten" cannot hide that two of them were
/// never examined. Defaulted, so a caller written before this existed still compiles and reads zero.
/// </param>
public sealed record ReviewDelta(
    int IterPrevia,
    int IterActual,
    int Nuevos,
    int Persisten,
    int Resueltos,
    int FueraDeAlcance = 0);
