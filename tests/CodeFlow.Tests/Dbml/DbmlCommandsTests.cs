using System.Text;
using System.Text.Json;
using CodeFlow.Ai;
using CodeFlow.Dbml;
using CodeFlow.Ipc;
using CodeFlow.Storage;
using CodeFlow.Tests.Files;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Dbml;

/// <summary>
/// The schema designer's command surface: finding <c>.dbml</c> documents in a folder that need not be
/// a repository (DBML-001), and remembering where a person put each table (DBML-005).
/// </summary>
public sealed class DbmlCommandsTests : IDisposable
{
    // A real migrated database: the layout table leans on the projects foreign key and its cascade,
    // and an in-memory stand-in would prove less than it looks (see TempDatabase).
    private readonly TempDatabase _database = new();

    public void Dispose() => _database.Dispose();

    /// <summary>Every command this domain owns, from <c>01-ipc-surface.md</c>.</summary>
    private static readonly string[] Expected =
        ["dbml_list_documents", "dbml_load_layout", "dbml_save_positions", "dbml_clear_layout", "dbml_assist"];

    [Fact]
    public void The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            Registry().Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("dbml_list_documents", "rootPath")]
    [InlineData("dbml_load_layout", "projectId")]
    [InlineData("dbml_save_positions", "projectId")]
    [InlineData("dbml_clear_layout", "projectId")]
    [InlineData("dbml_assist", "mode")]
    public async Task A_missing_argument_is_named_in_the_error(string command, string argument)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await InvokeAsync(command, new { }));

        Assert.Equal($"missing required parameter '{argument}'", error.Message);
    }

    // ---------- documents (DBML-001) ----------

    [Fact]
    public async Task Documents_come_back_project_relative_sorted_and_with_forward_slashes()
    {
        // Separators are normalised because this path is half of the layout key: the same file has
        // to be one row whether it was found on Windows or on macOS.
        using var folder = new TempDirectory();
        Write(folder, "schema.dbml", "Table a { id int }");
        Write(folder, "db/orders.dbml", "Table b { id int }");
        Write(folder, "db/nested/deep.dbml", "Table c { id int }");

        var reply = await InvokeAsync("dbml_list_documents", new { rootPath = folder.Path });

        Assert.Equal("""["db/nested/deep.dbml","db/orders.dbml","schema.dbml"]""", reply);
    }

    [Fact]
    public async Task A_folder_with_no_git_is_listed_like_any_other()
    {
        // The whole point (GIT-039): the schema designer has to work in a plain directory, which is
        // why this cannot reuse `list_repo_files` — that one opens a repository.
        using var folder = new TempDirectory();
        Write(folder, "model.dbml", "Table a { id int }");

        Assert.False(Directory.Exists(Path.Combine(folder.Path, ".git")));
        Assert.Equal("""["model.dbml"]""", await InvokeAsync("dbml_list_documents", new { rootPath = folder.Path }));
    }

    [Fact]
    public async Task Build_and_dependency_directories_are_never_descended_into()
    {
        // A schema under `node_modules` belongs to a dependency, not to the user. Pruned rather
        // than filtered, the reason DIVERGENCE-FILE-a gives.
        using var folder = new TempDirectory();
        Write(folder, "mine.dbml", "Table a { id int }");
        Write(folder, "node_modules/pkg/theirs.dbml", "Table b { id int }");
        Write(folder, "bin/generated.dbml", "Table c { id int }");
        Write(folder, ".git/odd.dbml", "Table d { id int }");

        Assert.Equal("""["mine.dbml"]""", await InvokeAsync("dbml_list_documents", new { rootPath = folder.Path }));
    }

    [Fact]
    public async Task The_extension_matches_whatever_case_it_was_written_in_and_nothing_else()
    {
        using var folder = new TempDirectory();
        Write(folder, "upper.DBML", "Table a { id int }");
        Write(folder, "notes.dbml.txt", "not a schema");
        Write(folder, "dbml", "not a schema either");

        Assert.Equal("""["upper.DBML"]""", await InvokeAsync("dbml_list_documents", new { rootPath = folder.Path }));
    }

    [Fact]
    public async Task An_empty_folder_answers_with_an_empty_list_rather_than_failing()
    {
        using var folder = new TempDirectory();

        Assert.Equal("[]", await InvokeAsync("dbml_list_documents", new { rootPath = folder.Path }));
    }

    [Fact]
    public async Task A_folder_that_is_not_there_says_so()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"codeflow-absent-{Guid.NewGuid():N}");

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await InvokeAsync("dbml_list_documents", new { rootPath = missing }));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    // ---------- layout (DBML-005) ----------

    [Fact]
    public async Task A_saved_position_comes_back_with_the_snake_case_keys_it_was_sent_with()
    {
        var projectId = CreateProject();

        await SaveAsync(projectId, "schema.dbml", new { table_key = "public.users", x = 10.5, y = 20.0 });

        Assert.Equal(
            """[{"table_key":"public.users","x":10.5,"y":20}]""",
            await InvokeAsync("dbml_load_layout", new { projectId, relPath = "schema.dbml" }));
    }

    [Fact]
    public async Task Saving_a_table_again_moves_it_rather_than_adding_a_second_row()
    {
        // The unique index is the conflict target: without it every drag would accumulate a row.
        var projectId = CreateProject();

        await SaveAsync(projectId, "schema.dbml", new { table_key = "public.users", x = 1.0, y = 1.0 });
        await SaveAsync(projectId, "schema.dbml", new { table_key = "public.users", x = 300.0, y = 40.0 });

        Assert.Equal(
            """[{"table_key":"public.users","x":300,"y":40}]""",
            await InvokeAsync("dbml_load_layout", new { projectId, relPath = "schema.dbml" }));
        Assert.Equal(1, CountRows());
    }

    [Fact]
    public async Task A_key_repeated_within_one_save_keeps_its_last_position()
    {
        var projectId = CreateProject();

        await SaveAsync(projectId, "schema.dbml",
            new { table_key = "public.users", x = 1.0, y = 1.0 },
            new { table_key = "public.users", x = 2.0, y = 2.0 });

        Assert.Equal(
            """[{"table_key":"public.users","x":2,"y":2}]""",
            await InvokeAsync("dbml_load_layout", new { projectId, relPath = "schema.dbml" }));
    }

    [Fact]
    public async Task Each_document_keeps_its_own_positions()
    {
        var projectId = CreateProject();

        await SaveAsync(projectId, "a.dbml", new { table_key = "public.users", x = 1.0, y = 1.0 });
        await SaveAsync(projectId, "b.dbml", new { table_key = "public.users", x = 9.0, y = 9.0 });

        Assert.Equal(
            """[{"table_key":"public.users","x":1,"y":1}]""",
            await InvokeAsync("dbml_load_layout", new { projectId, relPath = "a.dbml" }));
    }

    [Fact]
    public async Task Clearing_a_document_forgets_only_that_document()
    {
        // "Arrange everything" for one schema must not reset every other schema in the folder.
        var projectId = CreateProject();
        await SaveAsync(projectId, "a.dbml", new { table_key = "public.users", x = 1.0, y = 1.0 });
        await SaveAsync(projectId, "b.dbml", new { table_key = "public.users", x = 9.0, y = 9.0 });

        await InvokeAsync("dbml_clear_layout", new { projectId, relPath = "a.dbml" });

        Assert.Equal("[]", await InvokeAsync("dbml_load_layout", new { projectId, relPath = "a.dbml" }));
        Assert.Equal(
            """[{"table_key":"public.users","x":9,"y":9}]""",
            await InvokeAsync("dbml_load_layout", new { projectId, relPath = "b.dbml" }));
    }

    [Fact]
    public async Task Removing_the_project_removes_its_layouts()
    {
        // The cascade is what keeps a layout from outliving the folder it described.
        var projectId = CreateProject();
        await SaveAsync(projectId, "schema.dbml", new { table_key = "public.users", x = 1.0, y = 1.0 });

        _database.Do(c => ProjectStore.Delete(c, projectId));

        Assert.Equal(0, CountRows());
    }

    [Fact]
    public async Task An_empty_save_writes_nothing()
    {
        var projectId = CreateProject();

        Assert.Equal("null", await InvokeAsync("dbml_save_positions",
            new { projectId, relPath = "schema.dbml", positions = Array.Empty<object>() }));
        Assert.Equal(0, CountRows());
    }

    [Fact]
    public async Task A_position_without_a_table_key_is_refused_and_nothing_is_written()
    {
        var projectId = CreateProject();

        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await SaveAsync(projectId, "schema.dbml",
            new { table_key = "public.users", x = 1.0, y = 1.0 },
            new { table_key = "  ", x = 2.0, y = 2.0 }));

        Assert.Equal("a position is missing its table_key", error.Message);
        Assert.Equal(0, CountRows());
    }

    [Fact]
    public async Task A_save_for_a_project_that_does_not_exist_fails_on_the_foreign_key()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await SaveAsync("no-such-project", "schema.dbml", new { table_key = "public.users", x = 1.0, y = 1.0 }));

        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The slice as <c>Program.cs</c> wires it. The AI plumbing is real but never reached: only
    /// <c>dbml_assist</c> touches it, and that command's own behaviour is
    /// <see cref="DbmlAssistantTests"/>'s, driven through the engine seam instead of a subprocess.
    /// </summary>
    private CommandRegistry Registry() => new CommandRegistry()
        .AddDbmlCommands(_database.Handle, new AiRunRegistry((_, _, _) => ValueTask.CompletedTask), new HttpClient());

    private string CreateProject() =>
        _database.Use(c =>
        {
            var workspace = WorkspaceStore.Create(c, "schemas", "folder", "#6260ff");
            var input = new NewProject(
                workspace.Id, "schemas", "/tmp/schemas", RemoteUrl: null, "#6260ff", "folder",
                AdoOrg: null, AdoProject: null, AdoRepoId: null, GithubOwner: null, GithubRepo: null, GithubHost: null);

            return ProjectStore.Create(c, input).Id;
        });

    private long CountRows() =>
        _database.Use(c => Sql.Query(c, "SELECT COUNT(*) FROM dbml_layouts", r => r.GetInt64(0))[0]);

    private ValueTask<string> SaveAsync(string projectId, string relPath, params object[] positions) =>
        InvokeAsync("dbml_save_positions", new { projectId, relPath, positions });

    private static void Write(TempDirectory folder, string relativePath, string content)
    {
        var full = Path.Combine(folder.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// Drives the command through the registry, the way the transport does — which is what makes
    /// the argument names and the serialised shape part of what these tests assert.
    /// </summary>
    private async ValueTask<string> InvokeAsync(string command, object parameters)
    {
        Assert.True(Registry().TryGet(command, out var handler));

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var reply = await handler(arguments.RootElement, TestContext.Current.CancellationToken);

        return Encoding.UTF8.GetString(reply.Span);
    }
}
