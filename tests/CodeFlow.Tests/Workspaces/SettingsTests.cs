using CodeFlow.Ai;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// The generic settings store and the prompt cascade.
/// See <c>docs/business-rules/09-workspace-scoped.md</c> <c>WS-004</c> and
/// <c>03-storage.md</c> <c>STORE-012</c>.
/// </summary>
public sealed class SettingsTests
{
    [Fact]
    public void An_unset_key_reads_as_null()
    {
        using var db = new TempDatabase();

        Assert.Null(db.Use(c => Settings.GetSetting(c, "ai_provider")));
    }

    [Fact]
    public void A_stored_empty_value_is_a_real_row_and_reads_back_as_an_empty_string()
    {
        using var db = new TempDatabase();

        db.Do(c => Settings.SetSetting(c, "ai_provider", string.Empty));

        // WS-004: get_setting does not treat blank as unset. Readers that want that semantic apply
        // it at their own call site, and the routing cascade is the one that does.
        Assert.Equal(string.Empty, db.Use(c => Settings.GetSetting(c, "ai_provider")));
    }

    [Fact]
    public void Writing_a_key_twice_updates_it_in_place()
    {
        using var db = new TempDatabase();

        db.Do(c => Settings.SetSetting(c, "ai_provider", "claude"));
        db.Do(c => Settings.SetSetting(c, "ai_provider", "codex"));

        Assert.Equal("codex", db.Use(c => Settings.GetSetting(c, "ai_provider")));
    }

    [Theory]
    [InlineData("pr_description")]
    [InlineData("review_standard")]
    [InlineData("ticket_review_standard")]
    [InlineData("anything_unrecognised")]
    public void Every_prompt_kind_except_sdd_stages_has_non_empty_built_in_text(string kind) =>
        Assert.NotEqual(string.Empty, Settings.DefaultWorkspacePrompt(kind));

    /// <summary>
    /// The ticket standard needs its own arm, and this is what proves the arm is there.
    /// </summary>
    /// <remarks>
    /// The catch-all above it returns the PR methodology, so a missing arm would make "restore
    /// default" hand back a prompt that never mentions a work item and never emits the two verdict
    /// sections — without failing anywhere. The review would simply stop reporting criteria, which
    /// reads as the model refusing rather than as a settings bug.
    /// </remarks>
    [Fact]
    public void The_ticket_review_standard_does_not_fall_through_to_the_pr_one()
    {
        Assert.Equal(Prompts.DefaultTicketReviewStandard, Settings.DefaultWorkspacePrompt("ticket_review_standard"));
        Assert.NotEqual(Prompts.DefaultPrReviewStandard, Settings.DefaultWorkspacePrompt("ticket_review_standard"));
        Assert.Contains("## VEREDICTO DE COBERTURA", Settings.DefaultWorkspacePrompt("ticket_review_standard"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_workspace_is_seeded_with_the_ticket_review_standard()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        Assert.Equal(
            Prompts.DefaultTicketReviewStandard,
            db.Use(c => Settings.GetWorkspacePrompt(c, workspace.Id, "ticket_review_standard")));
    }

    [Fact]
    public void The_built_in_for_an_unrecognised_kind_is_the_review_methodology()
    {
        // 1.7.2's own shape is a catch-all `else`, not a validated enum — an unknown kind
        // resolves rather than failing, and it resolves to the same text as review_standard.
        Assert.Equal(Prompts.DefaultPrReviewStandard, Settings.DefaultWorkspacePrompt("review_standard"));
        Assert.Equal(Prompts.DefaultPrReviewStandard, Settings.DefaultWorkspacePrompt("anything_unrecognised"));
        Assert.Equal(Prompts.DefaultPrDescriptionTemplate, Settings.DefaultWorkspacePrompt("pr_description"));
    }

    [Fact]
    public void The_sdd_stages_built_in_is_empty()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        // The one kind for which get_workspace_prompt's "always non-empty" doc comment is wrong:
        // the SDD stages start blank because the user defines them.
        Assert.Equal(string.Empty, Settings.DefaultWorkspacePrompt("sdd_stages"));
        Assert.Equal(string.Empty, db.Use(c => Settings.GetWorkspacePrompt(c, workspace.Id, "sdd_stages")));
    }

    [Fact]
    public void Saving_a_blank_prompt_restores_the_built_in_without_deleting_the_row()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        db.Do(c => Settings.SetWorkspacePrompt(c, workspace.Id, "review_standard", "my own methodology"));
        Assert.Equal("my own methodology", db.Use(c => Settings.GetWorkspacePrompt(c, workspace.Id, "review_standard")));

        db.Do(c => Settings.SetWorkspacePrompt(c, workspace.Id, "review_standard", string.Empty));

        // STORE-012: the reset is an upsert to blank, never a delete. The row survives; the read
        // falls through to the built-in.
        Assert.Equal(
            Prompts.DefaultPrReviewStandard,
            db.Use(c => Settings.GetWorkspacePrompt(c, workspace.Id, "review_standard")));
        Assert.Equal(1L, db.Use(c => PromptRows(c, workspace.Id, "review_standard")));
    }

    [Fact]
    public void A_whitespace_only_prompt_counts_as_blank()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        db.Do(c => Settings.SetWorkspacePrompt(c, workspace.Id, "pr_description", "   \n\t "));

        Assert.Equal(
            Prompts.DefaultPrDescriptionTemplate,
            db.Use(c => Settings.GetWorkspacePrompt(c, workspace.Id, "pr_description")));
    }

    [Fact]
    public void A_workspace_with_no_row_for_a_kind_falls_through_to_the_built_in()
    {
        using var db = new TempDatabase();
        var workspace = db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));

        // Indistinguishable from an explicit blank save — which is the point: the two states
        // resolve identically, so "restore default" needs no delete path.
        Assert.Equal(0L, db.Use(c => PromptRows(c, workspace.Id, "sdd_stages")));
        Assert.Equal(string.Empty, db.Use(c => Settings.GetWorkspacePrompt(c, workspace.Id, "sdd_stages")));
    }

    private static long PromptRows(Microsoft.Data.Sqlite.SqliteConnection connection, string workspaceId, string kind)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM workspace_prompts WHERE workspace_id = $workspaceId AND kind = $kind";
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$kind", kind);
        return (long)command.ExecuteScalar()!;
    }
}
