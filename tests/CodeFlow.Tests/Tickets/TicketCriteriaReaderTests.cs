using System.Text.Json;
using CodeFlow.Tickets;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// Finding what a ticket actually asks for.
/// </summary>
/// <remarks>
/// The shapes here are the ones measured against a live Azure Boards organisation, not invented
/// ones: an acceptance-criteria field holding a hyphen, custom fields holding an unanswered
/// refinement form, and the requirements sitting in the description as prose.
/// </remarks>
public sealed class TicketCriteriaReaderTests
{
    private const string AcceptanceCriteria = "Microsoft.VSTS.Common.AcceptanceCriteria";

    private const string Description = "System.Description";

    private static JsonElement Fields(string json) => JsonDocument.Parse(json).RootElement;

    private static TicketCriteria Read(string fieldsJson, IReadOnlyList<string>? others = null) =>
        TicketCriteriaReader.Read(
            Fields(fieldsJson), TicketCriteriaReader.DefaultFields, others ?? []);

    // ---------- what the measurement found ----------

    [Fact]
    public void A_criteria_field_holding_a_hyphen_is_skipped_for_the_description()
    {
        // The real shape: the field exists, the team does not fill it, and the requirements are in
        // the description. Reading the field because it is named "acceptance criteria" would hand
        // the AI a hyphen to judge a branch against.
        var criteria = Read($$"""
            {
              "{{AcceptanceCriteria}}": "<div><b>-</b> </div>",
              "{{Description}}": "<div>La tabla no tiene horas de creación, así que cada archivo trae registros repetidos.</div>"
            }
            """);

        Assert.Equal(TicketCriteriaReader.ModeProse, criteria.Mode);
        Assert.Equal(Description, criteria.Field);
        Assert.Contains("registros repetidos", criteria.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_repeated_word_for_word_across_tickets_is_a_form_not_an_answer()
    {
        // Sixteen custom fields on a real board held identical text on every work item, because
        // they are the questions on the refinement template. Concatenating them would give the
        // model two thousand characters of unanswered questionnaire.
        const string Boilerplate =
            "<div>¿Qué debe hacer el proceso y con qué reglas de negocio? "
            + "En caso de haber condicionales ¿Qué sucede si se cumple?</div>";

        var others = new[] { $$"""{ "fields": { "{{AcceptanceCriteria}}": "{{Boilerplate}}" } }""" };

        var criteria = TicketCriteriaReader.Read(
            Fields($$"""
                {
                  "{{AcceptanceCriteria}}": "{{Boilerplate}}",
                  "{{Description}}": "<div>Crear dos campos nuevos en la tabla EpisodioSiniestro.</div>"
                }
                """),
            TicketCriteriaReader.DefaultFields,
            others);

        Assert.Equal(Description, criteria.Field);
        Assert.Contains("EpisodioSiniestro", criteria.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_other_ticket_to_compare_against_nothing_is_called_a_template()
    {
        // The comparison needs a corpus. Guessing without one would drop a real requirement the
        // first time a board is used, which is exactly when nobody would suspect the extraction.
        const string Text = "<div>Este texto es largo y podría ser una plantilla o no serlo.</div>";

        var criteria = Read($$"""{ "{{AcceptanceCriteria}}": "{{Text}}" }""");

        Assert.Equal(AcceptanceCriteria, criteria.Field);
    }

    // ---------- the substance filter ----------

    [Theory]
    [InlineData("<div><b>-</b> </div>")]
    [InlineData("<div>&nbsp;</div>")]
    [InlineData("<div>por definir</div>")]
    [InlineData("")]
    public void A_field_below_the_substance_floor_is_not_a_requirement(string html)
    {
        var criteria = Read($$"""{ "{{AcceptanceCriteria}}": "{{html}}" }""");

        Assert.Equal(TicketCriteriaReader.ModeNone, criteria.Mode);
        Assert.Null(criteria.Field);
    }

    [Fact]
    public void A_ticket_with_nothing_usable_says_none_rather_than_inventing_criteria()
    {
        var criteria = Read("""{ "System.Title": "Solo un título" }""");

        Assert.Equal(TicketCriteriaReader.ModeNone, criteria.Mode);
        Assert.Empty(criteria.Items);
        Assert.Equal(string.Empty, criteria.Markdown);
    }

    // ---------- the two modes ----------

    [Fact]
    public void An_explicit_list_is_numbered_deterministically()
    {
        var criteria = Read($$"""
            {
              "{{AcceptanceCriteria}}": "<ul><li>El usuario puede iniciar sesión con SSO</li><li>Se registra el intento fallido</li></ul>"
            }
            """);

        Assert.Equal(TicketCriteriaReader.ModeList, criteria.Mode);
        Assert.Equal(
            ["El usuario puede iniciar sesión con SSO", "Se registra el intento fallido"],
            criteria.Items);
    }

    [Fact]
    public void A_nested_bullet_extends_the_criterion_above_it_instead_of_becoming_its_own()
    {
        // A sub-case qualifies the rule it sits under. Promoting it would double-count that rule and
        // report a failure that belongs to the splitting rather than to the code.
        var criteria = Read($$"""
            {
              "{{AcceptanceCriteria}}": "<ul><li>La llave única es REFKEY</li><ul><li>Si es nuevo se inserta</li></ul></ul>"
            }
            """);

        var only = Assert.Single(criteria.Items);
        Assert.Contains("La llave única es REFKEY", only, StringComparison.Ordinal);
        Assert.Contains("Si es nuevo se inserta", only, StringComparison.Ordinal);
    }

    [Fact]
    public void Prose_is_left_whole_with_no_items_to_number()
    {
        var criteria = Read($$"""
            {
              "{{Description}}": "<div>Van dos observaciones sobre el flujo. Primero, la lógica de guardado debe cambiar.</div>"
            }
            """);

        Assert.Equal(TicketCriteriaReader.ModeProse, criteria.Mode);
        Assert.Empty(criteria.Items);
        Assert.Contains("Van dos observaciones", criteria.Markdown, StringComparison.Ordinal);
    }

    // ---------- the configured order ----------

    [Fact]
    public void The_configured_order_decides_which_field_wins()
    {
        var criteria = TicketCriteriaReader.Read(
            Fields($$"""
                {
                  "{{AcceptanceCriteria}}": "<div>Los criterios formales de aceptación de esta historia.</div>",
                  "Custom.Funcionamiento": "<div>Lo que este equipo escribe de verdad en su formulario.</div>"
                }
                """),
            ["Custom.Funcionamiento", AcceptanceCriteria],
            []);

        Assert.Equal("Custom.Funcionamiento", criteria.Field);
    }

    [Fact]
    public void A_field_that_is_not_a_string_is_stepped_over_rather_than_crashing()
    {
        // System.AssignedTo is an object and System.CommentCount a number. A field list naming one
        // by mistake must fall through to the next source, not fail the ticket.
        var criteria = TicketCriteriaReader.Read(
            Fields($$"""
                {
                  "System.AssignedTo": { "displayName": "Ada" },
                  "{{Description}}": "<div>El requisito real vive aquí abajo, con texto suficiente.</div>"
                }
                """),
            ["System.AssignedTo", Description],
            []);

        Assert.Equal(Description, criteria.Field);
    }

    [Fact]
    public void The_default_order_prefers_acceptance_criteria_when_it_is_actually_filled_in()
    {
        var criteria = Read($$"""
            {
              "{{AcceptanceCriteria}}": "<div>Debe rechazar el ingreso cuando el RUT no es válido.</div>",
              "{{Description}}": "<div>Contexto largo del ticket que no son los criterios.</div>"
            }
            """);

        Assert.Equal(AcceptanceCriteria, criteria.Field);
    }
}
