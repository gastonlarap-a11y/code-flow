using System.Text.Json;
using CodeFlow.Files;
using CodeFlow.Ipc;
using CodeFlow.Tests.Git;
using Xunit;

namespace CodeFlow.Tests.Files;

/// <summary>
/// The two commands from the implementation and the one from
/// the implementation, as the transport reaches them.
/// </summary>
public sealed class WatcherCommandsTests
{
    /// <summary>The exact set this group registers.</summary>
    private static readonly string[] Expected = ["start_watching", "stop_watching", "scan_staged_secrets"];

    [Fact]
    public async Task The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        await using var watcher = new RepoWatcher((_, _, _) => ValueTask.CompletedTask);
        var registry = new CommandRegistry().AddWatcherCommands(watcher);

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("start_watching")]
    [InlineData("stop_watching")]
    [InlineData("scan_staged_secrets")]
    public async Task A_command_missing_its_argument_names_the_one_it_wanted(string command)
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(command, new { }).AsTask());

        Assert.Equal("missing required parameter 'repoPath'", failure.Message);
    }

    [Fact]
    public async Task Stopping_a_repo_nobody_watches_answers_null_rather_than_failing()
    {
        Assert.Equal("null", await InvokeAsync("stop_watching", new { repoPath = "/no/such/repo" }));
    }

    /// <summary>
    /// The scanner's wire shape: <c>rule_name</c>, not <c>ruleName</c>.
    /// </summary>
    /// <remarks>
    /// <c>domain.ts</c> declares <c>SecretHit</c> with these field names, and
    /// <c>ChangesPanel.tsx</c> reads them. This is a different naming policy from the credential
    /// store's context next door, which is camelCase — the rule is to match what 1.7.2
    /// actually serialised.
    /// </remarks>
    [Fact]
    public async Task A_staged_secret_crosses_the_wire_under_the_field_names_the_renderer_reads()
    {
        using var repo = new TempRepo();
        repo.Write("config.ts", "const t = \"ghp_0123456789abcdefghijklmnopqrstuvwxyz\";\n");
        repo.Stage("config.ts");

        var reply = await InvokeAsync("scan_staged_secrets", new { repoPath = repo.Path });

        // The bullets travel as • escapes, as every non-ASCII character this sidecar sends
        // does — the review pipeline's Spanish strings included. JSON.parse restores them, so the
        // renderer sees the same string 1.7.2 sent it; only the bytes differ.
        var bullets = string.Concat(Enumerable.Repeat(@"\u2022", 16));

        Assert.Equal(
            $$"""[{"file":"config.ts","line":1,"rule":"github-token","rule_name":"GitHub token","severity":"critical","preview":"ghp{{bullets}}yz"}]""",
            reply);

        // And the value it decodes to is the masked preview the vectors specify.
        Assert.Equal("ghp••••••••••••••••yz", JsonDocument.Parse(reply).RootElement[0].GetProperty("preview").GetString());
    }

    [Fact]
    public async Task A_clean_staging_area_answers_an_empty_list()
    {
        using var repo = new TempRepo();
        repo.Write("a.ts", "const total = a + b;\n");
        repo.Stage("a.ts");

        Assert.Equal("[]", await InvokeAsync("scan_staged_secrets", new { repoPath = repo.Path }));
    }

    /// <summary>
    /// Only what is staged is scanned, which is what makes this a pre-commit gate.
    /// </summary>
    [Fact]
    public async Task An_unstaged_secret_is_not_what_this_commit_introduces()
    {
        using var repo = new TempRepo();
        repo.Write("config.ts", "const t = \"ghp_0123456789abcdefghijklmnopqrstuvwxyz\";\n");

        Assert.Equal("[]", await InvokeAsync("scan_staged_secrets", new { repoPath = repo.Path }));
    }

    /// <summary>Dispatches a command the way the transport does, and answers its JSON reply.</summary>
    private static async ValueTask<string> InvokeAsync(string command, object parameters)
    {
        await using var watcher = new RepoWatcher((_, _, _) => ValueTask.CompletedTask);
        var registry = new CommandRegistry().AddWatcherCommands(watcher);
        Assert.True(registry.TryGet(command, out var handler));

        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var reply = await handler(arguments.RootElement, TestContext.Current.CancellationToken);

        return System.Text.Encoding.UTF8.GetString(reply.Span);
    }
}
