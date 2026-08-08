using CodeFlow.Ipc;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// The command surface this slice owns: that every name exists, spelled exactly as the contract
/// says. See <c>docs/business-rules/01-ipc-surface.md</c>.
/// </summary>
public sealed class WorkspaceCommandsTests
{
    /// <summary>
    /// Every command registered from this feature.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from the registry: a typo in a name is invisible until the
    /// feature is used in the real app, where it surfaces as "unknown command" and nothing else.
    /// <c>pick_folder</c> is absent on purpose — it needs a native window and is answered by the
    /// shell's dialog bridge — and so are the review-memory commands that share
    /// the implementation, which ship with the review pipeline that fills their table.
    /// </remarks>
    private static readonly string[] Expected =
    [
        "default_clone_dir", "create_workspace", "list_workspaces", "delete_workspace",
        "rename_workspace", "update_workspace_color", "update_workspace_git_identity",
        "create_project", "list_projects", "get_project", "delete_project",
        "move_project_to_workspace", "update_project_color", "get_setting", "set_setting",
        "get_workspace_prompt", "set_workspace_prompt", "default_workspace_prompt",
        "list_workspace_agents", "upsert_workspace_agent", "delete_workspace_agent",
        "list_review_contexts", "upsert_review_context", "delete_review_context",
        "list_workspace_mcps", "upsert_workspace_mcp", "delete_workspace_mcp",
    ];

    [Fact]
    public void All_twenty_seven_commands_are_registered_under_their_contract_names()
    {
        using var db = new TempDatabase();
        var registry = new CommandRegistry().AddWorkspaceCommands(db.Handle);

        Assert.Equal(27, Expected.Length);
        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }
}
