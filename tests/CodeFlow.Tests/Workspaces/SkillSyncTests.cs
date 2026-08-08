using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// Copying a workspace's skills into a project (<c>WS-006</c>).
/// See <c>docs/business-rules/09-workspace-scoped.md</c> §"Skills subsystem".
/// </summary>
public sealed class SkillSyncTests
{
    [Fact]
    public void An_enabled_skill_is_copied_into_the_project()
    {
        using var fixture = new Fixture();
        fixture.Install("reviewer", "# Reviewer\n");

        SkillSync.Run([Skill("reviewer", enabled: true)], fixture.SourceRoot, fixture.ProjectPath);

        Assert.Equal("# Reviewer\n", File.ReadAllText(fixture.InProject("reviewer", "SKILL.md")));
    }

    [Fact]
    public void A_whole_skill_tree_comes_across_not_just_its_markdown()
    {
        using var fixture = new Fixture();
        fixture.Install("reviewer", "# Reviewer\n");
        Directory.CreateDirectory(Path.Combine(fixture.SourceRoot, "reviewer", "references"));
        File.WriteAllText(Path.Combine(fixture.SourceRoot, "reviewer", "references", "style.md"), "style");

        SkillSync.Run([Skill("reviewer", enabled: true)], fixture.SourceRoot, fixture.ProjectPath);

        Assert.Equal("style", File.ReadAllText(fixture.InProject("reviewer", "references", "style.md")));
    }

    /// <summary>
    /// Switching a skill off removes it from the project on the <em>next</em> sync.
    /// </summary>
    /// <remarks>
    /// Not when it is toggled: the roster is the only thing that changes then. That is why turning a
    /// skill off appears to take effect only when the next run starts.
    /// </remarks>
    [Fact]
    public void A_disabled_skill_is_removed_from_the_project_on_the_next_sync()
    {
        using var fixture = new Fixture();
        fixture.Install("reviewer", "# Reviewer\n");

        SkillSync.Run([Skill("reviewer", enabled: true)], fixture.SourceRoot, fixture.ProjectPath);
        Assert.True(File.Exists(fixture.InProject("reviewer", "SKILL.md")));

        SkillSync.Run([Skill("reviewer", enabled: false)], fixture.SourceRoot, fixture.ProjectPath);

        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.InProject("reviewer", "SKILL.md"))));
    }

    /// <summary>
    /// <b>The half of <c>WS-006</c> a careless port drops.</b>
    /// </summary>
    /// <remarks>
    /// What is managed is decided by name and by nothing else — no marker file, no manifest. A
    /// folder the user put in their own <c>.claude/skills</c> is never read, written or deleted, on
    /// either pass. Getting this wrong means the app silently eats work that was never its own.
    /// </remarks>
    [Fact]
    public void A_folder_this_workspace_does_not_know_about_is_never_touched()
    {
        using var fixture = new Fixture();
        fixture.Install("reviewer", "# Reviewer\n");

        var theirs = Path.Combine(fixture.ProjectPath, ".claude", "skills", "hand-written");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "SKILL.md"), "mine, not yours");

        // Both passes run: the copy pass, and the removal pass for a disabled skill.
        SkillSync.Run(
            [Skill("reviewer", enabled: true), Skill("retired", enabled: false)],
            fixture.SourceRoot,
            fixture.ProjectPath);

        Assert.Equal("mine, not yours", File.ReadAllText(Path.Combine(theirs, "SKILL.md")));
    }

    [Fact]
    public void A_skill_in_the_store_with_no_row_is_not_copied()
    {
        using var fixture = new Fixture();
        fixture.Install("reviewer", "# Reviewer\n");
        fixture.Install("orphan", "# Orphan\n");

        SkillSync.Run([Skill("reviewer", enabled: true)], fixture.SourceRoot, fixture.ProjectPath);

        // The store can hold a folder no row names — BUG-WS-a orphans one. Sync ignores it.
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, ".claude", "skills", "orphan")));
    }

    [Fact]
    public void An_empty_roster_creates_nothing_in_the_project()
    {
        using var fixture = new Fixture();

        SkillSync.Run([], fixture.SourceRoot, fixture.ProjectPath);

        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, ".claude")));
    }

    [Fact]
    public void Disabling_a_skill_that_was_never_synced_is_not_an_error()
    {
        using var fixture = new Fixture();

        SkillSync.Run([Skill("never-here", enabled: false)], fixture.SourceRoot, fixture.ProjectPath);
    }

    [Fact]
    public void A_re_sync_overwrites_what_it_wrote_before()
    {
        using var fixture = new Fixture();
        fixture.Install("reviewer", "# First\n");
        SkillSync.Run([Skill("reviewer", enabled: true)], fixture.SourceRoot, fixture.ProjectPath);

        File.WriteAllText(Path.Combine(fixture.SourceRoot, "reviewer", "SKILL.md"), "# Second\n");
        SkillSync.Run([Skill("reviewer", enabled: true)], fixture.SourceRoot, fixture.ProjectPath);

        Assert.Equal("# Second\n", File.ReadAllText(fixture.InProject("reviewer", "SKILL.md")));
    }

    private static WorkspaceSkill Skill(string name, bool enabled) =>
        new(Guid.NewGuid().ToString(), "ws-1", name, "custom", enabled, "2026-01-01T00:00:00Z");

    /// <summary>A workspace skill store and a project directory, both temporary.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("codeflow-sync-").FullName;

        public Fixture()
        {
            SourceRoot = Path.Combine(_root, "store", ".claude", "skills");
            ProjectPath = Path.Combine(_root, "project");

            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(ProjectPath);
        }

        public string SourceRoot { get; }

        public string ProjectPath { get; }

        public void Install(string name, string skillMd) => SkillFiles.CreateCustom(SourceRoot, name, skillMd);

        public string InProject(params string[] parts) =>
            Path.Combine([ProjectPath, ".claude", "skills", .. parts]);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
