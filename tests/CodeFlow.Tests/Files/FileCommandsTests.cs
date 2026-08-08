using System.Text.Json;
using CodeFlow.Files;
using CodeFlow.Ipc;
using CodeFlow.Tests.Git;
using Xunit;

namespace CodeFlow.Tests.Files;

/// <summary>
/// The thirteen commands from the implementation, as the transport reaches them.
/// See <c>docs/business-rules/01-ipc-surface.md</c>.
/// </summary>
public sealed class FileCommandsTests
{
    /// <summary>The exact set this group registers.</summary>
    private static readonly string[] Expected =
    [
        "list_dir", "read_file_text", "write_file_text", "write_file_bytes", "create_dir",
        "create_file", "move_path", "open_in_vscode", "open_in_default_app",
        "reveal_in_file_manager", "list_repo_files", "search_repo", "replace_in_repo",
    ];

    [Fact]
    public void The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        var registry = new CommandRegistry().AddFileCommands();

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("list_dir", "repoPath")]
    [InlineData("read_file_text", "repoPath")]
    [InlineData("write_file_bytes", "path")]
    [InlineData("create_dir", "repoPath")]
    [InlineData("open_in_vscode", "path")]
    [InlineData("reveal_in_file_manager", "path")]
    [InlineData("list_repo_files", "repoPath")]
    [InlineData("search_repo", "repoPath")]
    public async Task A_command_missing_its_argument_names_the_one_it_wanted(string command, string missing)
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(command, new { }).AsTask());

        Assert.Equal($"missing required parameter '{missing}'", failure.Message);
    }

    [Fact]
    public async Task An_export_wants_its_bytes_as_the_array_the_renderer_sends()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync("write_file_bytes", new { path = "/tmp/x.png" }).AsTask());

        Assert.Equal("missing required parameter 'contents'", failure.Message);
    }

    /// <summary>
    /// The wire shape of a listing: <c>is_dir</c>, not <c>isDir</c>.
    /// </summary>
    /// <remarks>
    /// <c>renderer/src/types/domain.ts</c> declares <c>FileEntry</c> with these field names,
    /// because the wire policy leaves it alone on the way out. Getting this wrong renders an
    /// explorer where nothing is a folder.
    /// </remarks>
    [Fact]
    public async Task A_listing_crosses_the_wire_under_the_field_names_the_renderer_reads()
    {
        using var repo = new TempDirectory();
        FileOps.CreateDir(repo.Path, "src");

        var reply = await InvokeAsync("list_dir", new { repoPath = repo.Path, subPath = (string?)null });

        Assert.Equal("""[{"name":"src","path":"src","is_dir":true}]""", reply);
    }

    [Fact]
    public async Task A_search_crosses_the_wire_with_snake_case_hits_and_a_truncation_flag()
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", "needle\n");

        var reply = await InvokeAsync("search_repo", new
        {
            repoPath = repo.Path,
            query = "needle",
            options = new { caseSensitive = false, wholeWord = false, regex = false, include = "", exclude = "" },
            maxResults = 50,
        });

        Assert.Equal("""{"hits":[{"path":"a.ts","line_no":1,"line":"needle"}],"truncated":false}""", reply);
    }

    /// <summary>
    /// A replace that changed nothing still reports <c>checkpoint_id</c> as an explicit null.
    /// </summary>
    /// <remarks>
    /// The renderer's type is <c>string | null</c>; a field dropped for being null reads as
    /// <c>undefined</c>, which is a different value to any code that checks for it.
    /// </remarks>
    [Fact]
    public async Task A_null_checkpoint_is_sent_rather_than_omitted()
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", "nothing here\n");

        var reply = await InvokeAsync("replace_in_repo", new
        {
            repoPath = repo.Path,
            query = "needle",
            replacement = "pin",
            options = new { caseSensitive = false, wholeWord = false, regex = false, include = "", exclude = "" },
            onlyPath = (string?)null,
        });

        Assert.Equal("""{"replacements":0,"files":0,"checkpoint_id":null}""", reply);
    }

    /// <summary>
    /// The find box's toggles arrive camelCase, which is the one shape 1.7.2 renames.
    /// </summary>
    [Fact]
    public async Task The_search_toggles_are_read_under_their_camel_case_names()
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", "const set = 1;\nconst offset = 2;\n");

        var reply = await InvokeAsync("search_repo", new
        {
            repoPath = repo.Path,
            query = "set",
            options = new { caseSensitive = true, wholeWord = true, regex = false, include = "*.ts", exclude = "" },
            maxResults = 50,
        });

        // wholeWord read as false would have matched `offset` on line 2 as well.
        Assert.Equal("""{"hits":[{"path":"a.ts","line_no":1,"line":"const set = 1;"}],"truncated":false}""", reply);
    }

    /// <summary>Dispatches a command the way the transport does, and answers its JSON reply.</summary>
    private static async ValueTask<string> InvokeAsync(string command, object parameters)
    {
        var registry = new CommandRegistry().AddFileCommands();
        Assert.True(registry.TryGet(command, out var handler));

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var reply = await handler(arguments.RootElement, TestContext.Current.CancellationToken);

        return System.Text.Encoding.UTF8.GetString(reply.Span);
    }
}
