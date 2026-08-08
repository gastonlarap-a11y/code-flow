using CodeFlow.Review;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// The two Spanish blocks a re-review's body is wrapped in.
/// See <c>docs/business-rules/07-review-pipeline.md</c> §Traceability rendering, <c>REVIEW-030</c>.
/// </summary>
/// <remarks>
/// Both are <c>VERBATIM</c>: the user reads them, and the stored <c>review_md</c> is exactly what
/// was returned, so a reworded bullet changes the saved record too. Asserted whole rather than by
/// fragment, which is the only way a stray space or a lost blank line fails a test.
/// </remarks>
public sealed class ReviewMemoryRenderTests
{
    [Fact]
    public void There_is_no_history_section_when_nothing_was_resolved_or_discarded()
    {
        Assert.Null(ReviewMemory.ResolvedHistorySection(
            [Finding("F-001", MemoryFinding.Open), Finding("F-002", MemoryFinding.Posted)]));
    }

    [Fact]
    public void Resolved_findings_render_the_traceability_history()
    {
        var section = ReviewMemory.ResolvedHistorySection([
            Finding("F-001", MemoryFinding.Resolved) with
            {
                Categoria = "Seguridad",
                Archivo = "src/auth.ts",
                IntroducidoEnIter = 1,
                ResueltoEnIter = 3,
            },
        ]);

        Assert.Equal(
            "\n\n---\n\n### 🕘 Historial de hallazgos resueltos (trazabilidad)\n\n"
            + "- `Seguridad` · src/auth.ts — introducido iter 1 · resuelto iter 3\n",
            section);
    }

    [Fact]
    public void A_resolved_finding_with_no_location_renders_a_dash()
    {
        var section = ReviewMemory.ResolvedHistorySection([
            Finding("F-001", MemoryFinding.Resolved) with
            {
                Categoria = "Estilo",
                Archivo = null,
                IntroducidoEnIter = 2,
                // Missing rather than zero — the fallback is what gets printed.
                ResueltoEnIter = null,
            },
        ]);

        Assert.Equal(
            "\n\n---\n\n### 🕘 Historial de hallazgos resueltos (trazabilidad)\n\n"
            + "- `Estilo` · — — introducido iter 2 · resuelto iter 0\n",
            section);
    }

    [Fact]
    public void Discarded_findings_render_their_own_section_with_the_reason()
    {
        var section = ReviewMemory.ResolvedHistorySection([
            Finding("F-001", MemoryFinding.FalsePositive) with
            {
                Categoria = "Seguridad",
                Archivo = "src/auth.ts",
                MotivoDescarte = "el token nunca sale del proceso",
            },
            Finding("F-002", MemoryFinding.Ignored) with { Categoria = "Estilo", Archivo = "src/ui.tsx" },
        ]);

        Assert.Equal(
            "\n### 🗂️ Hallazgos descartados\n\n"
            + "- `Seguridad` · src/auth.ts — falso positivo: el token nunca sale del proceso\n"
            + "- `Estilo` · src/ui.tsx — ignorado\n",
            section);
    }

    [Fact]
    public void Both_sections_render_together_resolved_first()
    {
        var section = ReviewMemory.ResolvedHistorySection([
            Finding("F-001", MemoryFinding.Ignored) with { Categoria = "Estilo", Archivo = "src/ui.tsx" },
            Finding("F-002", MemoryFinding.Resolved) with
            {
                Categoria = "Seguridad",
                Archivo = "src/auth.ts",
                IntroducidoEnIter = 1,
                ResueltoEnIter = 2,
            },
        ]);

        Assert.Equal(
            "\n\n---\n\n### 🕘 Historial de hallazgos resueltos (trazabilidad)\n\n"
            + "- `Seguridad` · src/auth.ts — introducido iter 1 · resuelto iter 2\n"
            + "\n### 🗂️ Hallazgos descartados\n\n"
            + "- `Estilo` · src/ui.tsx — ignorado\n",
            section);
    }

    [Fact]
    public void The_delta_banner_ends_in_a_blank_line_because_it_is_prepended_onto_the_body()
    {
        Assert.Equal(
            "🔁 Re-revisión (iter 2 → 3): 1 nuevos · 4 persisten · 2 resueltos\n\n",
            ReviewMemory.DeltaBanner(new ReviewDelta(2, 3, 1, 4, 2)));
    }

    [Fact]
    public void An_open_finding_this_run_never_restated_is_named_rather_than_only_counted()
    {
        // The gap this closes: a re-review only quotes what it found this time, so a finding whose
        // file had not moved was carried forward by `Reconcile` and appeared nowhere at all — the
        // banner said "2 persisten" and the reader had no way to learn which two.
        var carried = Finding("F-004", MemoryFinding.Posted) with
        {
            Categoria = "resumen-huerfano",
            Archivo = "src/CodeFlow.App/Review/ReviewPosting.cs",
            IntroducidoEnIter = 3,
        };

        var section = ReviewMemory.PersistingSection([carried], restated: []);

        Assert.Equal(
            "\n\n---\n\n### 📌 Siguen abiertos de revisiones anteriores\n\n"
            + "- `resumen-huerfano` · src/CodeFlow.App/Review/ReviewPosting.cs — F-004, introducido iter 3"
            + "; sin cambios en ese archivo desde entonces\n",
            section);
    }

    [Fact]
    public void A_finding_the_body_already_carries_is_not_repeated_underneath_it()
    {
        // Matched by identity — file plus category — because that is the key reconciliation itself
        // uses. Matching by position would have quietly depended on the order `Reconcile` merges in.
        var open = Finding("F-004", MemoryFinding.Posted) with
        {
            Categoria = "resumen-huerfano",
            Archivo = "src/Review/ReviewPosting.cs",
        };

        // The same finding as the model just wrote it: its own id, before renumbering.
        var restated = Finding("F-001", MemoryFinding.Open) with
        {
            Categoria = "resumen-huerfano",
            Archivo = "src/Review/ReviewPosting.cs",
        };

        Assert.Null(ReviewMemory.PersistingSection([open], [restated]));
    }

    [Fact]
    public void A_resolved_finding_belongs_to_the_history_and_not_to_the_open_list()
    {
        Assert.Null(ReviewMemory.PersistingSection([Finding("F-001", MemoryFinding.Resolved)], restated: []));
        Assert.Null(ReviewMemory.PersistingSection([Finding("F-002", MemoryFinding.FalsePositive)], restated: []));
    }

    private static MemoryFinding Finding(string id, string estado) => new()
    {
        Id = id,
        Severity = "warning",
        Tipo = "Bug",
        Categoria = "Seguridad",
        Subtitulo = "Algo",
        Estado = estado,
    };
}
