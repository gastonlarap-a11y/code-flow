using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// The <c>workspace_skills</c> rows, including the two documented defects this codebase reproduces.
/// See <c>docs/business-rules/91-known-bugs.md</c>, <c>BUG-WS-a</c> and <c>BUG-WS-b</c>.
/// </summary>
public sealed class SkillStoreTests
{
    [Fact]
    public void An_installed_skill_is_listed_in_installation_order_and_starts_enabled()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);

        database.Do(c =>
        {
            SkillStore.Add(c, workspace, "first", "acme/skills");
            SkillStore.Add(c, workspace, "second", "custom");
        });

        var skills = database.Use(c => SkillStore.List(c, workspace));

        Assert.Equal(["first", "second"], skills.Select(s => s.SkillName));
        Assert.All(skills, s => Assert.True(s.Enabled));
        Assert.Equal("acme/skills", skills[0].SourceRepo);
    }

    [Fact]
    public void A_skill_can_be_switched_off_without_being_removed()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);
        var skill = database.Use(c => SkillStore.Add(c, workspace, "reviewer", "custom"));

        database.Do(c => SkillStore.SetEnabled(c, skill.Id, false));

        var stored = database.Use(c => SkillStore.Get(c, skill.Id));
        Assert.False(stored!.Enabled);
        Assert.Equal("reviewer", stored.SkillName);
    }

    [Fact]
    public void Skills_are_scoped_to_their_workspace()
    {
        using var database = new TempDatabase();
        var one = Workspace(database, "One");
        var two = Workspace(database, "Two");

        database.Do(c => SkillStore.Add(c, one, "reviewer", "custom"));

        Assert.Empty(database.Use(c => SkillStore.List(c, two)));
    }

    [Fact]
    public void Deleting_a_workspace_takes_its_skills_with_it()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);
        database.Do(c => SkillStore.Add(c, workspace, "reviewer", "custom"));

        database.Do(c => WorkspaceStore.Delete(c, workspace));

        Assert.Empty(database.Use(c => SkillStore.List(c, workspace)));
    }

    /// <summary>
    /// <c>BUG-WS-b</c>, reproduced and not fixed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Installing from skills.sh has no existing-skill guard and the table has no
    /// <c>UNIQUE(workspace_id, skill_name)</c>, so reinstalling the same skill leaves two rows over
    /// one folder. Removing either one deletes that folder out from under the other, which then
    /// lists a skill whose files are gone.
    /// </para>
    /// <para>
    /// It is a defect rather than a policy because the other two creation paths <em>do</em> refuse a
    /// name already taken — see <c>SkillFilesTests</c>. Adding the constraint or the guard here
    /// would change what the app does, so the test states the defect instead.
    /// </para>
    /// </remarks>
    [Fact]
    public void Installing_the_same_skill_twice_leaves_two_rows_over_one_folder()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);

        database.Do(c =>
        {
            SkillStore.Add(c, workspace, "reviewer", "acme/skills");
            SkillStore.Add(c, workspace, "reviewer", "acme/skills");
        });

        var skills = database.Use(c => SkillStore.List(c, workspace));

        Assert.Equal(2, skills.Count);
        Assert.Equal(["reviewer", "reviewer"], skills.Select(s => s.SkillName));
        Assert.NotEqual(skills[0].Id, skills[1].Id);
    }

    /// <summary>
    /// <c>BUG-WS-a</c>, reproduced and not fixed: the row goes before the folder.
    /// </summary>
    /// <remarks>
    /// The removal deletes the row first and then discards whatever the filesystem says. When the
    /// directory cannot be deleted — open in an editor, locked on Windows — nothing is left that
    /// names it, and the name it occupies can no longer be reused, because both other creation paths
    /// refuse an existing directory. Here the folder is simply never created, which reaches the same
    /// state by the same route without needing to lock a directory the test cannot lock portably.
    /// </remarks>
    [Fact]
    public void Removing_a_folder_deletes_it_and_frees_its_name()
    {
        // BUG-WS-a, closed: the folder goes first and a real folder really goes — so the name is
        // reusable afterwards instead of orphaned behind a swallowed error.
        using var root = new TempSkillsRoot();
        SkillFiles.CreateCustom(root.Path, "reviewer", "# Reviewer\n");

        SkillFiles.RemoveDirectory(root.Path, "reviewer");

        Assert.False(Directory.Exists(Path.Combine(root.Path, "reviewer")));
        SkillFiles.CreateCustom(root.Path, "reviewer", "# Again\n");
        Assert.True(File.Exists(Path.Combine(root.Path, "reviewer", "SKILL.md")));
    }

    [Fact]
    public void Removing_a_skill_that_left_no_folder_behind_is_still_a_clean_removal()
    {
        using var database = new TempDatabase();
        using var root = new TempSkillsRoot();
        var workspace = Workspace(database);
        var skill = database.Use(c => SkillStore.Add(c, workspace, "reviewer", "custom"));

        // A folder that was never created is not an error: the goal state is reached.
        SkillFiles.RemoveDirectory(root.Path, "reviewer");
        database.Do(c => SkillStore.Delete(c, skill.Id));

        Assert.Empty(database.Use(c => SkillStore.List(c, workspace)));
    }

    private static string Workspace(TempDatabase database, string name = "Workspace") =>
        database.Use(c => WorkspaceStore.Create(c, name, "folder", "#ffffff")).Id;
}
