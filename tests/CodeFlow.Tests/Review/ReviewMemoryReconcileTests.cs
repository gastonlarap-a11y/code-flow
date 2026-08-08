using CodeFlow.Review;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// The two-pass state machine a re-review runs to merge a fresh parse against the previous run.
/// See <c>docs/business-rules/07-review-pipeline.md</c> §Reconciliation, <c>REVIEW-027</c>–<c>REVIEW-031</c>.
/// </summary>
public sealed class ReviewMemoryReconcileTests
{
    [Fact]
    public void An_unmatched_current_finding_is_new()
    {
        var (merged, delta) = ReviewMemory.Reconcile(
            previous: [Finding("F-001", "src/a.ts", "Seguridad")],
            current: [Finding("F-009", "src/b.ts", "Rendimiento")],
            previousIter: 1,
            changedFiles: null);

        var fresh = Assert.Single(merged, f => f.Delta == "nuevo");
        // Next after the highest correlative on either side, so it cannot collide with F-009.
        Assert.Equal("F-010", fresh.Id);
        Assert.Equal(MemoryFinding.Open, fresh.Estado);
        Assert.Equal(2, fresh.IntroducidoEnIter);
        Assert.Equal(1, delta.Nuevos);
    }

    [Fact]
    public void A_matching_finding_persists_and_keeps_everything_the_previous_run_knew()
    {
        var previous = Finding("F-003", "src/a.ts", "Seguridad") with
        {
            Estado = MemoryFinding.Posted,
            ThreadId = 42,
            IntroducidoEnIter = 2,
        };

        var (merged, delta) = ReviewMemory.Reconcile(
            [previous],
            // The model re-emitted it under a different id and with drifted line numbers.
            [Finding("F-001", "src/a.ts", "Seguridad") with { Lineas = "88-90" }],
            previousIter: 4,
            changedFiles: null);

        var persisted = Assert.Single(merged);
        Assert.Equal("F-003", persisted.Id);
        Assert.Equal(MemoryFinding.Posted, persisted.Estado);
        Assert.Equal(42, persisted.ThreadId);
        Assert.Equal(2, persisted.IntroducidoEnIter);
        Assert.Equal("persiste", persisted.Delta);
        // The fresh parse still wins on everything the model just re-observed.
        Assert.Equal("88-90", persisted.Lineas);
        Assert.Equal(1, delta.Persisten);
    }

    [Fact]
    public void A_persisting_finding_from_before_iteration_tracking_gets_a_plausible_one()
    {
        // 0 is the "not assigned yet" sentinel a pre-tracking row carries.
        var (merged, _) = ReviewMemory.Reconcile(
            [Finding("F-001", "src/a.ts", "Seguridad") with { IntroducidoEnIter = 0 }],
            [Finding("F-001", "src/a.ts", "Seguridad")],
            previousIter: 3,
            changedFiles: null);

        Assert.Equal(3, Assert.Single(merged).IntroducidoEnIter);
    }

    [Fact]
    public void A_pre_tracking_row_on_the_very_first_iteration_still_gets_at_least_one()
    {
        var (merged, _) = ReviewMemory.Reconcile(
            [Finding("F-001", "src/a.ts", "Seguridad") with { IntroducidoEnIter = 0 }],
            [Finding("F-001", "src/a.ts", "Seguridad")],
            previousIter: 0,
            changedFiles: null);

        Assert.Equal(1, Assert.Single(merged).IntroducidoEnIter);
    }

    [Fact]
    public void A_persisting_finding_that_a_human_discarded_keeps_that_mark_and_is_not_counted()
    {
        var (merged, delta) = ReviewMemory.Reconcile(
            [
                Finding("F-001", "src/a.ts", "Seguridad") with
                {
                    Estado = MemoryFinding.FalsePositive,
                    MotivoDescarte = "el token nunca sale del proceso",
                },
            ],
            [Finding("F-001", "src/a.ts", "Seguridad")],
            previousIter: 1,
            changedFiles: null);

        var persisted = Assert.Single(merged);
        Assert.Equal(MemoryFinding.FalsePositive, persisted.Estado);
        Assert.Equal("el token nunca sale del proceso", persisted.MotivoDescarte);
        // Only an active finding counts toward "persisten".
        Assert.Equal(0, delta.Persisten);
    }

    [Fact]
    public void A_finding_that_reappears_after_being_resolved_is_treated_as_brand_new()
    {
        var resolved = Finding("F-001", "src/a.ts", "Seguridad") with
        {
            Estado = MemoryFinding.Resolved,
            ThreadId = 7,
            IntroducidoEnIter = 1,
            ResueltoEnIter = 2,
        };

        var (merged, delta) = ReviewMemory.Reconcile(
            [resolved],
            [Finding("F-001", "src/a.ts", "Seguridad")],
            previousIter: 2,
            changedFiles: null);

        // Two rows: the brand-new one, and the old resolved one re-emitted for traceability.
        Assert.Equal(2, merged.Count);

        var fresh = Assert.Single(merged, f => f.Delta == "nuevo");
        Assert.Equal("F-002", fresh.Id);
        Assert.Equal(MemoryFinding.Open, fresh.Estado);
        Assert.Equal(3, fresh.IntroducidoEnIter);
        // The old thread is deliberately NOT carried over — a later post opens a new one rather
        // than reopening the thread that was closed when the finding was fixed.
        Assert.Null(fresh.ThreadId);

        var history = Assert.Single(merged, f => f.Estado == MemoryFinding.Resolved);
        Assert.Equal("F-001", history.Id);
        Assert.Equal(7, history.ThreadId);
        Assert.Equal("persiste", history.Delta);

        Assert.Equal(1, delta.Nuevos);
        Assert.Equal(0, delta.Persisten);
    }

    [Fact]
    public void An_active_previous_finding_that_did_not_resurface_is_resolved()
    {
        var (merged, delta) = ReviewMemory.Reconcile(
            [Finding("F-001", "src/a.ts", "Seguridad") with { ThreadId = 5, IntroducidoEnIter = 1 }],
            current: [],
            previousIter: 1,
            changedFiles: null);

        var resolved = Assert.Single(merged);
        Assert.Equal(MemoryFinding.Resolved, resolved.Estado);
        Assert.Equal(2, resolved.ResueltoEnIter);
        Assert.Equal("resuelto", resolved.Delta);
        // The thread survives, so the posting flow can reply on it.
        Assert.Equal(5, resolved.ThreadId);
        Assert.Equal(1, delta.Resueltos);
    }

    [Fact]
    public void On_an_efficient_re_review_a_finding_whose_file_was_not_touched_persists_instead()
    {
        var (merged, delta) = ReviewMemory.Reconcile(
            [Finding("F-001", "src/untouched.ts", "Seguridad")],
            current: [],
            previousIter: 1,
            changedFiles: ["src/other.ts"]);

        var carried = Assert.Single(merged);
        // Its code was never re-analysed, so it cannot have been observed fixed.
        Assert.Equal(MemoryFinding.Open, carried.Estado);
        Assert.Equal("persiste", carried.Delta);
        Assert.Equal(0, delta.Resueltos);
        Assert.Equal(1, delta.Persisten);
    }

    [Fact]
    public void A_finding_with_no_location_always_counts_as_re_analysed()
    {
        var (merged, delta) = ReviewMemory.Reconcile(
            [Finding("F-001", archivo: null, "Seguridad")],
            current: [],
            previousIter: 1,
            changedFiles: ["src/other.ts"]);

        Assert.Equal(MemoryFinding.Resolved, Assert.Single(merged).Estado);
        Assert.Equal(1, delta.Resueltos);
    }

    [Theory]
    // Either side may be the more fully qualified one, and a leading slash never matters.
    [InlineData("src/app.ts", "src/app.ts")]
    [InlineData("src/app.ts", "/src/app.ts")]
    [InlineData("src/app.ts", "repo/src/app.ts")]
    [InlineData("repo/src/app.ts", "src/app.ts")]
    [InlineData("SRC/App.ts", "src/app.ts")]
    public void The_changed_file_match_is_suffix_tolerant(string findingFile, string changedFile)
    {
        var (merged, _) = ReviewMemory.Reconcile(
            [Finding("F-001", findingFile, "Seguridad")],
            current: [],
            previousIter: 1,
            changedFiles: [changedFile]);

        Assert.Equal(MemoryFinding.Resolved, Assert.Single(merged).Estado);
    }

    [Fact]
    public void An_already_resolved_previous_finding_is_carried_forward_untouched()
    {
        var resolved = Finding("F-001", "src/a.ts", "Seguridad") with
        {
            Estado = MemoryFinding.Resolved,
            IntroducidoEnIter = 1,
            ResueltoEnIter = 2,
        };

        var (merged, delta) = ReviewMemory.Reconcile([resolved], current: [], previousIter: 3, changedFiles: null);

        var carried = Assert.Single(merged);
        Assert.Equal(MemoryFinding.Resolved, carried.Estado);
        Assert.Equal(2, carried.ResueltoEnIter);
        Assert.Equal("persiste", carried.Delta);
        // Traceability only — it is not re-evaluated and counts toward nothing.
        Assert.Equal(0, delta.Resueltos);
        Assert.Equal(0, delta.Persisten);
    }

    [Fact]
    public void Two_findings_that_share_a_file_and_a_category_get_one_previous_row_each()
    {
        // BUG-REVIEW-b, fixed after parity. The identity key `{file}|{category}` is not injective —
        // one file holding two security findings is ordinary — and matching took the first previous
        // row for both, so they came out sharing a stable id and a thread id. Two findings in one
        // thread, and an id that was not stable.
        //
        // Only one previous row exists here, so exactly one current finding may claim it; the other
        // is genuinely new and must be told so.
        var previous = Finding("F-001", "src/a.ts", "Seguridad") with
        {
            Estado = MemoryFinding.Posted,
            ThreadId = 11,
            IntroducidoEnIter = 1,
        };

        var (merged, delta) = ReviewMemory.Reconcile(
            [previous],
            [
                Finding("F-001", "src/a.ts", "Seguridad") with { Subtitulo = "El token viaja en la URL" },
                Finding("F-002", "src/a.ts", "Seguridad") with { Subtitulo = "La cookie no es HttpOnly" },
            ],
            previousIter: 1,
            changedFiles: null);

        Assert.Equal(2, merged.Count);

        // Distinct ids, and only one of them inherits the thread. Posting the other one opens its
        // own thread instead of replying into a conversation about a different finding.
        Assert.Equal(2, merged.Select(f => f.Id).Distinct().Count());
        Assert.Single(merged, f => f.ThreadId == 11);
        Assert.Single(merged, f => f.ThreadId is null);

        var persisted = Assert.Single(merged, f => f.Delta == "persiste");
        Assert.Equal("F-001", persisted.Id);

        var fresh = Assert.Single(merged, f => f.Delta == "nuevo");
        Assert.Equal(2, fresh.IntroducidoEnIter);

        Assert.Equal(1, delta.Nuevos);
        Assert.Equal(1, delta.Persisten);
    }

    [Fact]
    public void An_active_previous_row_is_preferred_over_a_resolved_one_with_the_same_identity()
    {
        // The ordering half of BUG-REVIEW-b. With a resolved row sitting before an active one under
        // the same identity, taking whichever came first filed a finding that had been open all
        // along as brand new — losing its thread, its introduction iteration, and any human decision
        // recorded against it.
        var resolved = Finding("F-001", "src/a.ts", "Seguridad") with
        {
            Estado = MemoryFinding.Resolved,
            ResueltoEnIter = 1,
        };

        var active = Finding("F-002", "src/a.ts", "Seguridad") with
        {
            Estado = MemoryFinding.Posted,
            ThreadId = 22,
            IntroducidoEnIter = 1,
        };

        var (merged, _) = ReviewMemory.Reconcile(
            [resolved, active],
            [Finding("F-009", "src/a.ts", "Seguridad")],
            previousIter: 1,
            changedFiles: null);

        var persisted = Assert.Single(merged, f => f.Delta == "persiste" && f.ThreadId == 22);
        Assert.Equal("F-002", persisted.Id);
        Assert.Equal(1, persisted.IntroducidoEnIter);
    }

    [Fact]
    public void A_finding_with_neither_a_location_nor_a_category_falls_back_to_its_subtitle()
    {
        var previous = Finding("F-001", archivo: null, categoria: "") with
        {
            Subtitulo = "Falta manejo de error",
            Estado = MemoryFinding.Posted,
            ThreadId = 3,
        };

        var (merged, _) = ReviewMemory.Reconcile(
            [previous],
            // Same subtitle, different case: still the same finding.
            [Finding("F-005", archivo: null, categoria: "") with { Subtitulo = "FALTA MANEJO DE ERROR" }],
            previousIter: 1,
            changedFiles: null);

        var persisted = Assert.Single(merged);
        Assert.Equal("F-001", persisted.Id);
        Assert.Equal(3, persisted.ThreadId);
    }

    [Fact]
    public void A_finding_with_a_category_but_no_file_does_not_fall_back()
    {
        // Only the exactly-empty key falls back. Same category, different subtitle → still a match.
        var previous = Finding("F-001", archivo: null, "Seguridad") with { Subtitulo = "Uno" };

        var (merged, _) = ReviewMemory.Reconcile(
            [previous],
            [Finding("F-004", archivo: null, "Seguridad") with { Subtitulo = "Otro completamente distinto" }],
            previousIter: 1,
            changedFiles: null);

        Assert.Equal("F-001", Assert.Single(merged).Id);
    }

    [Fact]
    public void The_delta_reports_the_two_iterations_it_compared()
    {
        var (_, delta) = ReviewMemory.Reconcile([], [], previousIter: 4, changedFiles: null);

        Assert.Equal(4, delta.IterPrevia);
        Assert.Equal(5, delta.IterActual);
    }

    // ---------- DIVERGENCE-REVIEW-b: the level a finding was found at ----------

    [Fact]
    public void A_shallower_re_review_does_not_call_an_unexamined_finding_resolved()
    {
        // The scenario the operator decided to close: `ultra` surfaces something a `basico` pass does
        // not even look for, and the next `basico` run would have reported it fixed. "Gone" and "not
        // examined" are different answers, and only one of them is true.
        var found = Finding("F-001", "src/a.ts", "Mantenibilidad") with { Nivel = "ultra" };

        var (merged, delta) = ReviewMemory.Reconcile(
            previous: [found],
            current: [],
            previousIter: 1,
            changedFiles: ["src/a.ts"],
            level: "basico");

        var carried = Assert.Single(merged);
        Assert.Equal("fuera_de_alcance", carried.Delta);
        Assert.Equal(MemoryFinding.Open, carried.Estado);
        Assert.Null(carried.ResueltoEnIter);
        Assert.Equal(0, delta.Resueltos);
        Assert.Equal(1, delta.FueraDeAlcance);

        // Counted in both, because it did persist — the separate number is what stops "1 persisten"
        // from hiding that nothing looked at it.
        Assert.Equal(1, delta.Persisten);
    }

    [Fact]
    public void A_re_review_at_the_same_depth_or_deeper_still_resolves()
    {
        // The other side of the rule. Without this, closing REVIEW-b would have quietly disabled
        // resolution altogether, which nothing would have reported as a failure.
        foreach (var level in new[] { "completo", "ultra" })
        {
            var found = Finding("F-001", "src/a.ts", "Seguridad") with { Nivel = "completo" };

            var (merged, delta) = ReviewMemory.Reconcile(
                [found], [], previousIter: 1, changedFiles: ["src/a.ts"], level: level);

            Assert.Equal("resuelto", Assert.Single(merged).Delta);
            Assert.Equal(1, delta.Resueltos);
            Assert.Equal(0, delta.FueraDeAlcance);
        }
    }

    [Fact]
    public void A_finding_stored_before_levels_existed_behaves_exactly_as_it_did()
    {
        // Findings written before this field existed deserialise with an empty level. Treating that
        // as "deeper than anything" would retroactively stop resolving in every existing history.
        var found = Finding("F-001", "src/a.ts", "Seguridad");
        Assert.Equal("", found.Nivel);

        var (merged, delta) = ReviewMemory.Reconcile(
            [found], [], previousIter: 1, changedFiles: ["src/a.ts"], level: "basico");

        Assert.Equal("resuelto", Assert.Single(merged).Delta);
        Assert.Equal(1, delta.Resueltos);
    }

    [Fact]
    public void A_re_found_finding_records_the_depth_that_just_saw_it()
    {
        // Found by `ultra`, seen again by `completo` → demonstrably visible at `completo`, so a later
        // `completo` run may resolve it. Keeping the original depth forever would freeze it.
        var found = Finding("F-001", "src/a.ts", "Seguridad") with { Nivel = "ultra" };

        var (merged, _) = ReviewMemory.Reconcile(
            [found],
            [Finding("F-001", "src/a.ts", "Seguridad")],
            previousIter: 1,
            changedFiles: ["src/a.ts"],
            level: "completo");

        Assert.Equal("completo", Assert.Single(merged).Nivel);
    }

    [Fact]
    public void The_banner_only_mentions_out_of_scope_when_there_is_some()
    {
        var quiet = ReviewMemory.DeltaBanner(new ReviewDelta(1, 2, 0, 1, 0));
        var noisy = ReviewMemory.DeltaBanner(new ReviewDelta(1, 2, 0, 1, 0, 1));

        Assert.DoesNotContain("fuera de alcance", quiet, StringComparison.Ordinal);
        Assert.Contains("1 fuera de alcance a este nivel", noisy, StringComparison.Ordinal);
    }

    private static MemoryFinding Finding(string id, string? archivo, string categoria) => new()
    {
        Id = id,
        Severity = "warning",
        Tipo = "Bug",
        Categoria = categoria,
        Subtitulo = "Algo",
        Archivo = archivo,
    };
}
