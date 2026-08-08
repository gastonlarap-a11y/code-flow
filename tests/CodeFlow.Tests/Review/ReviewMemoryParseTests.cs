using CodeFlow.Review;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// Turning a review's markdown into comparable findings.
/// See <c>docs/business-rules/07-review-pipeline.md</c> §Finding parsing, <c>REVIEW-024</c>–<c>REVIEW-026</c>.
/// </summary>
/// <remarks>
/// CodeFlow 1.7.2 carries no tests for any of this — the implementation has zero
/// extracted vectors to lean on and every case here is
/// authored against the source and the rules document.
/// </remarks>
public sealed class ReviewMemoryParseTests
{
    [Fact]
    public void A_header_yields_severity_type_category_and_the_models_own_id()
    {
        var findings = ReviewMemory.ParseFindings(
            "### 🚨 [Blocker · Bug] Seguridad · F-001\nEl token viaja en la URL.\n");

        var finding = Assert.Single(findings);
        Assert.Equal("critical", finding.Severity);
        Assert.Equal("Bug", finding.Tipo);
        Assert.Equal("Seguridad", finding.Categoria);
        Assert.Equal("F-001", finding.Id);
        Assert.Equal("El token viaja en la URL.", finding.Subtitulo);
    }

    [Theory]
    [InlineData("Blocker", "critical")]
    [InlineData("Crítico", "critical")]
    [InlineData("Mayor", "warning")]
    [InlineData("Menor", "info")]
    [InlineData("Info", "info")]
    public void The_word_in_the_brackets_is_what_decides_the_severity(string severity, string expected)
    {
        // Emoji deliberately wrong in every case: the word is the one the model reasoned about, and
        // the emoji is decoration derived from it — three symbols standing in for five levels.
        var findings = ReviewMemory.ParseFindings($"### ℹ️ [{severity} · Code Smell] Estilo · F-007\n");

        Assert.Equal(expected, Assert.Single(findings).Severity);
    }

    [Theory]
    [InlineData("🚨", "critical")]
    [InlineData("⚠️", "warning")]
    [InlineData("ℹ️", "info")]
    public void An_unrecognised_severity_word_falls_back_to_the_emoji(string emoji, string expected)
    {
        // 1.7.2's own vocabulary drifted — "Alta", "Media" — and a review stored under it must keep
        // parsing exactly as it did.
        var findings = ReviewMemory.ParseFindings($"### {emoji} [Alta · Code Smell] Estilo · F-007\n");

        Assert.Equal(expected, Assert.Single(findings).Severity);
    }

    [Fact]
    public void A_severity_the_model_contradicts_with_its_own_emoji_follows_the_word()
    {
        // Measured on this repository's pull request #60: the model emitted `🚨 [Mayor · Security
        // Hotspot]`, against the emoji mapping its own prompt gives it. Read from the emoji, two
        // `Mayor` findings were stored as critical and the Quality Gate went red for them.
        var findings = ReviewMemory.ParseFindings(
            "### 🚨 [Mayor · Security Hotspot] secretos-en-log-persistente · F-011\n");

        Assert.Equal("warning", Assert.Single(findings).Severity);
    }

    [Fact]
    public void Every_field_is_read_from_its_own_block()
    {
        // The point of slicing by header offsets: finding 2's location must not bleed into finding 1,
        // which is what a document-wide search for 📍 would do.
        var findings = ReviewMemory.ParseFindings(
            """
            ### 🚨 [Blocker · Bug] Seguridad · F-001
            Primero.

            ### ⚠️ [Mayor · Bug] Rendimiento · F-002
            Segundo.
            📍 Ubicación: src/app.ts:12-14
            🎯 Confianza: 80
            """);

        Assert.Equal(2, findings.Count);
        Assert.Null(findings[0].Archivo);
        Assert.Null(findings[0].Confianza);
        Assert.Equal("src/app.ts", findings[1].Archivo);
        Assert.Equal("12-14", findings[1].Lineas);
        Assert.Equal(80, findings[1].Confianza);
    }

    [Fact]
    public void The_subtitle_skips_the_structured_fields()
    {
        var findings = ReviewMemory.ParseFindings(
            """
            ### ⚠️ [Mayor · Bug] Rendimiento · F-002

            📍 Ubicación: src/app.ts:12
            💭 Por qué: la consulta corre dentro del bucle.
            La consulta se repite por cada fila.
            """);

        Assert.Equal("La consulta se repite por cada fila.", Assert.Single(findings).Subtitulo);
    }

    [Fact]
    public void A_block_with_nothing_but_structured_fields_has_an_empty_subtitle()
    {
        var findings = ReviewMemory.ParseFindings(
            "### ℹ️ [Menor · Code Smell] Estilo · F-003\n📍 Ubicación: src/a.ts:1\n");

        Assert.Equal("", Assert.Single(findings).Subtitulo);
    }

    [Theory]
    // The split is on the last colon, and only when a digit follows it.
    [InlineData("src/app.ts:12-14", "src/app.ts", "12-14")]
    [InlineData("`src/app.ts`:12", "src/app.ts", "12")]
    [InlineData("**src/a_b.ts**:9", "src/ab.ts", "9")]
    [InlineData("C:/repo/src/app.ts:12", "C:/repo/src/app.ts", "12")]
    public void A_location_with_a_line_number_splits_into_file_and_lines(
        string raw, string expectedFile, string expectedLines)
    {
        var findings = ReviewMemory.ParseFindings(
            $"### 🚨 [Blocker · Bug] Seguridad · F-001\n📍 Ubicación: {raw}\n");

        var finding = Assert.Single(findings);
        Assert.Equal(expectedFile, finding.Archivo);
        Assert.Equal(expectedLines, finding.Lineas);
    }

    [Theory]
    // No colon at all, or a tail with no digit in it: the whole cleaned string is the file.
    [InlineData("src/app.ts", "src/app.ts")]
    [InlineData("C:", "C:")]
    [InlineData("src/app.ts:linea", "src/app.ts:linea")]
    public void A_location_with_no_line_number_stays_a_bare_file(string raw, string expectedFile)
    {
        var findings = ReviewMemory.ParseFindings(
            $"### 🚨 [Blocker · Bug] Seguridad · F-001\n📍 Ubicación: {raw}\n");

        var finding = Assert.Single(findings);
        Assert.Equal(expectedFile, finding.Archivo);
        Assert.Null(finding.Lineas);
    }

    [Fact]
    public void The_unaccented_spelling_of_ubicacion_is_accepted_too()
    {
        var findings = ReviewMemory.ParseFindings(
            "### 🚨 [Blocker · Bug] Seguridad · F-001\n📍 Ubicacion: src/app.ts:3\n");

        Assert.Equal("src/app.ts", Assert.Single(findings).Archivo);
    }

    [Fact]
    public void An_unparsable_confidence_is_simply_absent()
    {
        var findings = ReviewMemory.ParseFindings(
            "### 🚨 [Blocker · Bug] Seguridad · F-001\n🎯 Confianza: alta\n");

        Assert.Null(Assert.Single(findings).Confianza);
    }

    [Fact]
    public void A_fresh_parse_carries_the_sentinel_iteration_and_the_open_state()
    {
        var finding = Assert.Single(ReviewMemory.ParseFindings(
            "### 🚨 [Blocker · Bug] Seguridad · F-001\nAlgo.\n"));

        Assert.Equal(MemoryFinding.Open, finding.Estado);
        // 0 means "not assigned yet"; only reconciliation or the first-run path fills it in.
        Assert.Equal(0, finding.IntroducidoEnIter);
        Assert.Null(finding.ThreadId);
        Assert.Null(finding.ResueltoEnIter);
        Assert.Null(finding.MotivoDescarte);
        Assert.Null(finding.Delta);
    }

    [Fact]
    public void Text_that_is_not_a_finding_header_yields_nothing()
    {
        Assert.Empty(ReviewMemory.ParseFindings(
            "## Resumen\n\nTodo se ve bien.\n\n### Una sección cualquiera\n\nNada que reportar.\n"));
    }

    [Fact]
    public void Windows_line_endings_still_match_the_header()
    {
        // The trailing \s* before $ is what absorbs the carriage return, in both languages.
        var findings = ReviewMemory.ParseFindings(
            "### 🚨 [Blocker · Bug] Seguridad · F-001\r\nEl token viaja en la URL.\r\n");

        Assert.Equal("F-001", Assert.Single(findings).Id);
    }

    [Fact]
    public void Identity_normalises_the_leading_slash_and_the_case()
    {
        Assert.Equal("src/app.ts|seguridad", ReviewMemory.FindingIdentity("/Src/App.ts", "Seguridad"));
        Assert.Equal("|seguridad", ReviewMemory.FindingIdentity(null, "Seguridad"));
        Assert.Equal("src/app.ts|", ReviewMemory.FindingIdentity("src/app.ts", ""));
    }
}
