namespace CodeFlow.Ai;

/// <summary>
/// Which code a review of local changes is looking at.
/// </summary>
/// <remarks>
/// <para>
/// One of the two axes the AI panel exposes; the other is whether the work item is part of the
/// question. They used to be coupled — the pre-commit analysis was always the working tree and never
/// the ticket, the ticket review was always the whole branch — so two of the four combinations did
/// not exist. The one that was missed most is <see cref="Branch"/> without a ticket: reviewing what
/// you have before opening a pull request, in a repository that uses no tickets at all.
/// </para>
/// <para>
/// It reaches the model as a labelled <c>SCOPE:</c> line rather than through the prompt, because
/// <c>analyze_template</c> is a user-editable setting: baking the scope into the built-in text would
/// leave anybody who had edited theirs describing the wrong diff.
/// </para>
/// </remarks>
internal enum ReviewScope
{
    /// <summary>The working tree against the index and HEAD — what is not committed yet.</summary>
    Working,

    /// <summary>The merge base against the working tree — everything the branch contributes.</summary>
    Branch,
}

/// <summary>The wire spellings of <see cref="ReviewScope"/> and what each one tells the model.</summary>
/// <remarks>
/// The descriptions are Spanish because they are payload the model reads and answers in Spanish —
/// the same exemption the prompts carry (see <c>Prompts</c>). The instructions around them are
/// English like the rest of the codebase.
/// </remarks>
internal static class ReviewScopes
{
    /// <summary>Reads the wire value, defaulting to the cheaper scope rather than throwing.</summary>
    /// <remarks>
    /// An unrecognised value is the working tree because that is the smaller, faster answer and the
    /// one the panel starts on: guessing the whole branch would spend a model's budget on a request
    /// that was already malformed.
    /// </remarks>
    public static ReviewScope Parse(string? value) =>
        string.Equals(value, "branch", StringComparison.OrdinalIgnoreCase) ? ReviewScope.Branch : ReviewScope.Working;

    /// <summary>What the <c>SCOPE:</c> line says the diff is.</summary>
    public static string Describe(ReviewScope scope) => scope switch
    {
        ReviewScope.Branch =>
            "el aporte completo de la rama — desde su punto de divergencia con la rama base hasta el "
            + "árbol de trabajo, así que incluye lo ya commiteado además de lo pendiente",
        _ =>
            "sólo los cambios que todavía no están commiteados — el árbol de trabajo contra el índice "
            + "y HEAD, con los archivos nuevos incluidos",
    };

    /// <summary>
    /// The sentence that keeps a working-tree scope from producing systematic false negatives.
    /// </summary>
    /// <remarks>
    /// <b>The one guard rail this feature could not do without.</b> Judging only the uncommitted diff
    /// against a ticket's acceptance criteria means the evidence for everything already committed on
    /// the branch is simply not in front of the model — so it answers <c>no cumple</c> to criteria
    /// that are, in fact, met. That is not the false positive the user accepted; it is systematic,
    /// and it discredits the whole verdict. Empty for a branch scope, which has the evidence.
    /// </remarks>
    public static string CriteriaCaveat(ReviewScope scope) => scope switch
    {
        ReviewScope.Working =>
            "\n\nAVISO SOBRE EL ALCANCE: este diff NO incluye lo que ya está commiteado en la rama. "
            + "Un criterio puede estar cumplido por trabajo anterior que no se te está mostrando, así "
            + "que la ausencia de evidencia aquí NO es evidencia de ausencia: en ese caso responde "
            + "`no verificable`, nunca `no cumple`.",
        _ => string.Empty,
    };
}
