using CodeFlow.Ai;
using CodeFlow.Dbml;
using CodeFlow.Tests.Ai;
using Xunit;

namespace CodeFlow.Tests.Dbml;

/// <summary>
/// The schema designer's assistant (DBML-016): what it builds for the engine, and what it refuses
/// to send at all.
/// </summary>
/// <remarks>
/// Driven through the <see cref="AiRunner"/> seam with <see cref="ScriptedEngine"/>, so what is
/// asserted is the invocation this code assembles — the prompt/stdin split (<c>AI-002</c>), the
/// system prompt each mode selects, and the shaping of the reply.
/// </remarks>
public sealed class DbmlAssistantTests
{
    private const string Schema = """
                                  Table usuarios {
                                    id integer [pk]
                                  }
                                  """;

    // ---------- what reaches the engine ----------

    [Fact]
    public async Task The_ask_rides_on_argv_and_the_schema_on_stdin()
    {
        // AI-002. A schema pasted into an argument would break several of the CLIs outright.
        var engine = ScriptedEngine.Answering(Schema);

        await AssistAsync(engine, "review", Schema, "");

        Assert.DoesNotContain("Table usuarios", engine.Only.Prompt, StringComparison.Ordinal);
        Assert.Contains("Table usuarios", engine.Only.StdinContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("review")]
    [InlineData("explain")]
    public async Task Each_mode_sends_its_own_system_prompt(string mode)
    {
        var engine = ScriptedEngine.Answering(Schema);

        await AssistAsync(engine, mode, Schema, "añade una tabla");

        var expected = mode switch
        {
            "edit" => Prompts.DbmlEditPrompt,
            "review" => Prompts.DbmlReviewPrompt,
            _ => Prompts.DbmlExplainPrompt,
        };

        Assert.Equal(expected, engine.Only.SystemPrompt);
    }

    [Fact]
    public async Task A_blank_instruction_is_left_out_of_the_payload_rather_than_sent_as_an_empty_heading()
    {
        // A section with nothing under it reads as a question the user declined to answer.
        var engine = ScriptedEngine.Answering("# Explicación");

        await AssistAsync(engine, "explain", Schema, "   ");

        Assert.DoesNotContain("INSTRUCCIÓN", engine.Only.StdinContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_instruction_is_sent_after_the_schema()
    {
        var engine = ScriptedEngine.Answering(Schema);

        await AssistAsync(engine, "edit", Schema, "añade una tabla mascotas");

        var payload = engine.Only.StdinContent;
        Assert.Contains("añade una tabla mascotas", payload, StringComparison.Ordinal);
        Assert.True(
            payload.IndexOf("ESQUEMA DBML", StringComparison.Ordinal)
            < payload.IndexOf("INSTRUCCIÓN", StringComparison.Ordinal),
            "the last thing the model reads should be what it was asked for");
    }

    [Fact]
    public async Task A_long_instruction_is_capped_while_the_schema_is_not_truncated()
    {
        // The schema cannot be usefully cut: the edit mode is asked to return the whole document,
        // so a dropped tail comes back as a proposal that deletes every table past the cut.
        var engine = ScriptedEngine.Answering(Schema);
        var instruction = new string('a', 5_000);

        await AssistAsync(engine, "edit", Schema, instruction);

        Assert.DoesNotContain(instruction, engine.Only.StdinContent, StringComparison.Ordinal);
        Assert.Contains(Schema, engine.Only.StdinContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_assistant_reaches_for_no_tools()
    {
        // It is handed the whole document; a tool call could only re-read what it already has, or
        // wander into a folder that may not even be a repository.
        var engine = ScriptedEngine.Answering(Schema);

        await AssistAsync(engine, "review", Schema, "");

        Assert.Empty(engine.Only.Tools);
    }

    // ---------- what comes back ----------

    [Fact]
    public async Task An_edit_answer_loses_the_code_fence_a_model_wraps_it_in()
    {
        // AI-018: this text replaces the user's file, so backticks in it would be written to disk.
        var engine = ScriptedEngine.Answering($"```dbml\n{Schema}\n```");

        Assert.Equal(Schema, await AssistAsync(engine, "edit", Schema, "renombra la tabla"));
    }

    [Fact]
    public async Task A_review_keeps_the_code_blocks_inside_its_markdown()
    {
        // The opposite rule, and the reason the two modes are shaped apart: a review proposes DBML
        // lines in fenced blocks, and stripping the first one would eat a finding.
        var answer = "## Integridad\n\n- Falta la FK:\n\n```dbml\nRef: a.b > c.d\n```\n";
        var engine = ScriptedEngine.Answering(answer);

        Assert.Equal(answer.Trim(), await AssistAsync(engine, "review", Schema, ""));
    }

    // ---------- what never reaches the engine ----------

    [Fact]
    public async Task An_empty_document_is_refused_before_anything_runs()
    {
        var engine = ScriptedEngine.Answering(Schema);

        var error = await Assert.ThrowsAsync<AiRunFailedException>(async () =>
            await AssistAsync(engine, "review", "  \n ", ""));

        Assert.Equal("El documento está vacío", error.Message);
        Assert.Empty(engine.Invocations);
    }

    [Fact]
    public async Task An_edit_with_no_instruction_is_refused_because_it_has_no_meaning()
    {
        var engine = ScriptedEngine.Answering(Schema);

        var error = await Assert.ThrowsAsync<AiRunFailedException>(async () =>
            await AssistAsync(engine, "edit", Schema, "   "));

        Assert.Equal("Escribe qué quieres cambiar en el esquema", error.Message);
        Assert.Empty(engine.Invocations);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("explain")]
    public async Task The_other_two_modes_stand_on_their_own_with_no_instruction(string mode)
    {
        var engine = ScriptedEngine.Answering("respuesta");

        Assert.Equal("respuesta", await AssistAsync(engine, mode, Schema, ""));
    }

    [Fact]
    public async Task A_schema_past_the_cap_is_refused_and_the_error_says_by_how_much()
    {
        var engine = ScriptedEngine.Answering(Schema);
        var huge = new string('x', 60_001);

        var error = await Assert.ThrowsAsync<AiRunFailedException>(async () =>
            await AssistAsync(engine, "review", huge, ""));

        Assert.Contains("60001", error.Message, StringComparison.Ordinal);
        Assert.Contains("60000", error.Message, StringComparison.Ordinal);
        Assert.Empty(engine.Invocations);
    }

    [Fact]
    public async Task An_unknown_mode_names_itself_in_the_error()
    {
        // What a renderer/backend drift looks like from a log.
        var engine = ScriptedEngine.Answering(Schema);

        var error = await Assert.ThrowsAsync<AiRunFailedException>(async () =>
            await AssistAsync(engine, "refactor", Schema, "algo"));

        Assert.Equal("unknown DBML assist mode 'refactor'", error.Message);
        Assert.Empty(engine.Invocations);
    }

    private static Task<string> AssistAsync(ScriptedEngine engine, string mode, string dbml, string instruction) =>
        DbmlAssistant.AssistAsync(
            engine.Runner, engine.Config(), mode, dbml, instruction, run: null,
            TestContext.Current.CancellationToken);
}
