using System.Text.Json;
using CodeFlow.Ai;
using CodeFlow.Ipc;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// The ten commands from the implementation, as the transport reaches them.
/// See <c>docs/business-rules/01-ipc-surface.md</c>.
/// </summary>
public sealed class SkillCommandsTests
{
    /// <summary>The exact set this group registers.</summary>
    private static readonly string[] Expected =
    [
        "list_workspace_skills", "install_workspace_skill", "remove_workspace_skill",
        "set_workspace_skill_enabled", "create_custom_skill", "import_skill_from_folder",
        "list_skill_files", "read_skill_file", "write_skill_file", "delete_skill_file",
    ];

    [Fact]
    public void The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        using var database = new TempDatabase();
        var registry = new CommandRegistry()
            .AddSkillCommands(database.Handle, new SkillInstaller((_, _, _) => ValueTask.CompletedTask));

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("list_workspace_skills", "workspaceId")]
    [InlineData("install_workspace_skill", "workspaceId")]
    [InlineData("remove_workspace_skill", "id")]
    [InlineData("set_workspace_skill_enabled", "id")]
    [InlineData("create_custom_skill", "workspaceId")]
    [InlineData("import_skill_from_folder", "workspaceId")]
    [InlineData("list_skill_files", "workspaceId")]
    [InlineData("read_skill_file", "workspaceId")]
    [InlineData("write_skill_file", "workspaceId")]
    [InlineData("delete_skill_file", "workspaceId")]
    public async Task A_command_missing_its_argument_names_the_one_it_wanted(string command, string missing)
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(command, new { }).AsTask());

        Assert.Equal($"missing required parameter '{missing}'", failure.Message);
    }

    [Fact]
    public async Task Toggling_a_skill_wants_a_boolean_and_says_so()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync("set_workspace_skill_enabled", new { id = "x" }).AsTask());

        Assert.Equal("missing required parameter 'enabled'", failure.Message);
    }

    [Fact]
    public async Task Removing_a_skill_that_does_not_exist_says_so()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync("remove_workspace_skill", new { id = "no-such-skill" }).AsTask());

        Assert.Equal("Skill not found", failure.Message);
    }

    /// <summary>
    /// The wire shape of a skill: <c>skill_name</c>, not <c>skillName</c>.
    /// </summary>
    /// <remarks>
    /// <c>SkillsSettings.tsx</c> reads these field names, because the wire policy leaves it alone
    /// on the way out. Getting this wrong renders a settings tab of blank rows rather than an error.
    /// </remarks>
    [Fact]
    public async Task A_roster_crosses_the_wire_under_the_field_names_the_renderer_reads()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;
        var skill = database.Use(c => SkillStore.Add(c, workspace, "reviewer", "acme/skills"));

        var reply = await InvokeAsync("list_workspace_skills", new { workspaceId = workspace }, database);

        using var parsed = JsonDocument.Parse(reply);
        var row = Assert.Single(parsed.RootElement.EnumerateArray().ToArray());

        // The field names are the contract; their order is what the record declares.
        Assert.Equal(
            ["id", "workspace_id", "skill_name", "source_repo", "enabled", "installed_at"],
            row.EnumerateObject().Select(p => p.Name));

        Assert.Equal(skill.Id, row.GetProperty("id").GetString());
        Assert.Equal(workspace, row.GetProperty("workspace_id").GetString());
        Assert.Equal("reviewer", row.GetProperty("skill_name").GetString());
        Assert.Equal("acme/skills", row.GetProperty("source_repo").GetString());
        Assert.True(row.GetProperty("enabled").GetBoolean());
        Assert.Equal(skill.InstalledAt, row.GetProperty("installed_at").GetString());
    }

    [Fact]
    public async Task An_empty_roster_answers_an_empty_list()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;

        Assert.Equal("[]", await InvokeAsync("list_workspace_skills", new { workspaceId = workspace }, database));
    }

    /// <summary>
    /// <c>WS-005</c>: the Windows shim is not a style choice.
    /// </summary>
    /// <remarks>
    /// <c>npx</c> is a <c>.cmd</c> shim there and does not start when spawned directly, so the
    /// command has to go through <c>cmd /C</c>. Simplifying it breaks skill installation on the one
    /// platform nothing here can test, which is why it is pinned as a constant instead.
    /// </remarks>
    [Fact]
    public void The_installer_reaches_npx_the_way_each_platform_needs()
    {
        var start = SkillInstaller.NpxCommand();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd", start.FileName);
            Assert.Equal(["/C", "npx"], start.ArgumentList);
        }
        else
        {
            // Off Windows the name is resolved to wherever npx actually is, so that launching
            // searches the same space detection does (XLANG-AI-a). On a machine without npx there
            // is nothing to resolve to and the bare name is kept, which is the pre-existing
            // behaviour — so both outcomes are correct and the assertion has to allow either.
            Assert.Equal(BinaryDiscovery.FindOnPath("npx") ?? "npx", start.FileName);
            Assert.EndsWith("npx", start.FileName, StringComparison.Ordinal);
            Assert.Empty(start.ArgumentList);
        }
    }

    /// <summary>Dispatches a command the way the transport does, and answers its JSON reply.</summary>
    /// <summary>
    /// BUG-WS-a, closed: the folder goes first and its failure propagates, so the row survives
    /// an undeletable folder and the remove can be retried once the obstruction is gone.
    /// </summary>
    /// <remarks>
    /// Writes under the real <c>AppPaths</c> root — <c>RootFor</c> has no injection point — but
    /// namespaced by a fresh GUID workspace and removed in <c>finally</c>; a crash leaves only an
    /// inert directory no DB row points at. How "undeletable" is made differs per OS: Unix
    /// removes the directory's write bit, Windows holds an open handle inside it.
    /// </remarks>
    [Fact]
    public async Task An_undeletable_folder_fails_the_remove_and_keeps_the_row_for_a_retry()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;
        var root = SkillFiles.RootFor(workspace);
        var folder = SkillFiles.Directory(root, "stubborn");

        Directory.CreateDirectory(folder);
        var inner = Path.Combine(folder, "SKILL.md");
        File.WriteAllText(inner, "content");
        var skill = database.Use(c => SkillStore.Add(c, workspace, "stubborn", "acme/skills"));

        try
        {
            FileStream? hold = null;
            if (OperatingSystem.IsWindows())
            {
                hold = new FileStream(inner, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            else
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            try
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => InvokeAsync("remove_workspace_skill", new { id = skill.Id }, database).AsTask());

                Assert.StartsWith("Could not delete the skill's folder", failure.Message, StringComparison.Ordinal);
                Assert.NotNull(database.Use(c => SkillStore.Get(c, skill.Id)));
                Assert.True(Directory.Exists(folder));
            }
            finally
            {
                if (OperatingSystem.IsWindows())
                {
                    hold!.Dispose();
                }
                else
                {
                    File.SetUnixFileMode(
                        folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            // Freed, the same command completes: folder gone, then row gone.
            await InvokeAsync("remove_workspace_skill", new { id = skill.Id }, database);
            Assert.Null(database.Use(c => SkillStore.Get(c, skill.Id)));
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(root))!, recursive: true);
            }
        }
    }

    /// <summary>
    /// BUG-WS-b, closed: the install refuses an existing skill name, exactly like its two
    /// sibling creation paths always did — instead of running npx over the shared folder and
    /// adding a duplicate row.
    /// </summary>
    [Fact]
    public async Task Installing_over_an_existing_skill_name_is_refused_before_npx_runs()
    {
        using var database = new TempDatabase();
        var workspace = database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;
        var root = SkillFiles.RootFor(workspace);
        Directory.CreateDirectory(SkillFiles.Directory(root, "reviewer"));
        database.Do(c => SkillStore.Add(c, workspace, "reviewer", "acme/skills"));

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InvokeAsync(
                    "install_workspace_skill",
                    new { workspaceId = workspace, sourceRepo = "acme/skills", skillName = "reviewer" },
                    database).AsTask());

            Assert.Equal("A skill named \"reviewer\" already exists in this workspace", failure.Message);
            Assert.Single(database.Use(c => SkillStore.List(c, workspace)));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(root))!, recursive: true);
        }
    }

    private static async ValueTask<string> InvokeAsync(
        string command, object parameters, TempDatabase? database = null)
    {
        using var owned = database is null ? new TempDatabase() : null;
        var handle = (database ?? owned!).Handle;

        var registry = new CommandRegistry()
            .AddSkillCommands(handle, new SkillInstaller((_, _, _) => ValueTask.CompletedTask));

        Assert.True(registry.TryGet(command, out var handler));

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var reply = await handler(arguments.RootElement, TestContext.Current.CancellationToken);

        return System.Text.Encoding.UTF8.GetString(reply.Span);
    }
}
