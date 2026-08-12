using CodeFlow.Review;
using CodeFlow.Tickets;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// The acceptance-criteria verdict parser, and the proof that it does not disturb the finding
/// parser it shares a document with. <c>XLANG-001</c>, <c>XLANG-016</c>, <c>WI-012</c>.
/// </summary>
public sealed class TicketVerdictTests
{
    private const string Findings = """
        📈 CALIDAD: Fiabilidad=C Seguridad=A Mantenibilidad=B

        ### ⚠️ [Mayor · Bug] off-by-one · F-001

        El bucle deja fuera el último elemento.

        📍 Ubicación: src/lib/paginate.ts:20-24

        💭 Por qué: el índice final es exclusivo.

        💡 Sugerencia: usar <=.

        🎯 Confianza: 80/100
        """;

    private const string Verdict = """
        ## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN

        ### AC-1: El listado pagina de 20 en 20
        Veredicto: cumple
        Evidencia: src/lib/paginate.ts:12-18 — el tamaño de página se lee de la configuración
        🎯 Confianza: 85/100

        ### AC-2: El rendimiento no baja de 200 ms
        Veredicto: no verificable
        Evidencia: sin evidencia en el diff
        🎯 Confianza: 70/100

        ## VEREDICTO DE COBERTURA

        Cobertura: incompleta
        Faltante: la medición de rendimiento que pide el AC-2
        Fuera de alcance: nada
        Resumen: la paginación está implementada y probada; el criterio de latencia
        no puede comprobarse leyendo el cambio.
        """;

    private static string Full => $"{Findings}\n\n{Verdict}";

    [Fact]
    public void The_two_slices_are_disjoint()
    {
        var (findings, verdict) = TicketVerdict.Split(Full);

        Assert.Equal(Findings, findings);
        Assert.DoesNotContain("AC-1", findings, StringComparison.Ordinal);
        Assert.NotNull(verdict);
        Assert.StartsWith(TicketVerdict.CriteriaHeading, verdict);
    }

    [Fact]
    public void A_review_without_the_section_is_returned_whole()
    {
        var (findings, verdict) = TicketVerdict.Split(Findings);

        Assert.Equal(Findings, findings);
        Assert.Null(verdict);
        Assert.Null(TicketVerdict.Parse(Findings));
    }

    [Fact]
    public void Every_criterion_comes_back_with_its_verdict_evidence_and_confidence()
    {
        var parsed = TicketVerdict.Parse(Full);

        Assert.NotNull(parsed);
        Assert.Collection(
            parsed.Criteria,
            first =>
            {
                Assert.Equal("AC-1", first.Id);
                Assert.Equal("El listado pagina de 20 en 20", first.Criterion);
                Assert.Equal("cumple", first.Verdict);
                Assert.Equal(
                    "src/lib/paginate.ts:12-18 — el tamaño de página se lee de la configuración",
                    first.Evidence);
                Assert.Equal(85, first.Confidence);
            },
            second =>
            {
                Assert.Equal("AC-2", second.Id);
                Assert.Equal("no verificable", second.Verdict);
                Assert.Equal("sin evidencia en el diff", second.Evidence);
            });
    }

    [Fact]
    public void A_ticket_that_does_not_describe_the_change_says_so()
    {
        // The case a user hit: a fixture ticket from another project was linked, one of its
        // sentences ("if the key exists it is updated") matched a real upsert in the diff, and the
        // review answered `cumple` with full confidence off that coincidence. Neither verdict is
        // honest for a ticket nobody aimed at — the answer is that the ticket is the wrong one.
        var parsed = TicketVerdict.Parse(
            $"""
             {TicketVerdict.CriteriaHeading}

             No se puntúan: el ticket no corresponde a este cambio.

             {TicketVerdict.CoverageHeading}

             Relevancia: no corresponde — el ticket habla de importar archivos y el diff toca work items
             Cobertura: no verificable
             Faltante: —
             Fuera de alcance: —
             Resumen: revisa el work item vinculado.
             """);

        Assert.NotNull(parsed?.Coverage);
        Assert.False(parsed.Coverage.Relevant);
        Assert.StartsWith("no corresponde", parsed.Coverage.Relevance, StringComparison.Ordinal);
        Assert.Empty(parsed.Criteria);
    }

    [Fact]
    public void A_review_that_never_answered_the_relevance_question_counts_as_relevant()
    {
        // Silence is not a disavowal. Reviews stored before the question existed keep their meaning,
        // and a model that skipped the line does not have its verdict thrown away — only an explicit
        // "no corresponde" disowns the ticket.
        var coverage = TicketVerdict.Parse(Full)?.Coverage;

        Assert.NotNull(coverage);
        Assert.True(coverage.Relevant);
        Assert.Equal(string.Empty, coverage.Relevance);
    }

    [Fact]
    public void The_coverage_block_joins_a_summary_that_wrapped()
    {
        var coverage = TicketVerdict.Parse(Full)?.Coverage;

        Assert.NotNull(coverage);
        Assert.Equal("incompleta", coverage.Coverage);
        Assert.Equal("la medición de rendimiento que pide el AC-2", coverage.Missing);
        Assert.Equal("nada", coverage.OutOfScope);
        Assert.Equal(
            "la paginación está implementada y probada; el criterio de latencia no puede comprobarse leyendo el cambio.",
            coverage.Summary);
    }

    [Theory]
    [InlineData("cumple", "cumple")]
    [InlineData("**cumple**", "cumple")]
    [InlineData("`no cumple`", "no cumple")]
    [InlineData("Parcial", "parcial")]
    [InlineData("no verificable", "no verificable")]
    // Anything unreadable is the conservative answer, never `cumple`: a verdict the parser could not
    // read is not evidence that the work was done. Same table as `parseTicketVerdict.ts`.
    [InlineData("quizás", "no verificable")]
    [InlineData("", "no verificable")]
    public void A_verdict_word_is_normalised_towards_caution(string written, string expected)
    {
        var parsed = TicketVerdict.Parse(
            $"{TicketVerdict.CriteriaHeading}\n\n### AC-1: Algo\nVeredicto: {written}\nEvidencia: ninguna\n");

        Assert.NotNull(parsed);
        Assert.Equal(expected, Assert.Single(parsed.Criteria).Verdict);
    }

    [Fact]
    public void A_ticket_with_no_criteria_still_yields_a_coverage_verdict()
    {
        // The `mode: "none"` ticket of `WI-007`: the model is told to say so and to emit the
        // coverage block anyway, because a missing section is indistinguishable from a truncated
        // answer.
        var parsed = TicketVerdict.Parse(
            $"""
             {TicketVerdict.CriteriaHeading}

             El ticket no declara criterios verificables.

             {TicketVerdict.CoverageHeading}

             Cobertura: no verificable
             Faltante: nada
             Fuera de alcance: nada
             Resumen: sin criterios que juzgar.
             """);

        Assert.NotNull(parsed);
        Assert.Empty(parsed.Criteria);
        Assert.Equal("no verificable", parsed.Coverage?.Coverage);
    }

    [Fact]
    public void The_criteria_survive_a_coverage_block_that_never_arrived()
    {
        var parsed = TicketVerdict.Parse($"{TicketVerdict.CriteriaHeading}\n\n### AC-1: Algo\nVeredicto: cumple\n");

        Assert.NotNull(parsed);
        Assert.Single(parsed.Criteria);
        Assert.Null(parsed.Coverage);
    }

    /// <summary>
    /// The non-regression that the whole design rests on: <c>XLANG-001</c> is untouched.
    /// </summary>
    /// <remarks>
    /// <c>### AC-1:</c> carries none of the three things <c>ReviewMemory</c>'s header pattern needs —
    /// an emoji, a bracketed severity, an <c>F-NNN</c> id — so the criteria section reads to it as
    /// ordinary prose. Asserted against the <em>unsplit</em> text deliberately: the split is the
    /// second line of defence, and this proves the first one holds on its own.
    /// </remarks>
    [Fact]
    public void ParseFindings_reads_the_same_findings_with_or_without_the_verdict_section()
    {
        var withSection = ReviewMemory.ParseFindings(Full);
        var without = ReviewMemory.ParseFindings(Findings);

        Assert.Equal(without.Count, withSection.Count);
        Assert.Equal("F-001", Assert.Single(withSection).Id);
        Assert.Equal(without[0].Archivo, withSection[0].Archivo);
        Assert.Equal(without[0].Lineas, withSection[0].Lineas);
        Assert.Equal(without[0].Confianza, withSection[0].Confianza);
        Assert.Equal(without[0].Subtitulo, withSection[0].Subtitulo);
    }
}
