using System.Globalization;
using System.Text;
using CodeFlow.Providers.Azure;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// Assembling a pull request's diff from its blobs, which Azure DevOps has no endpoint for.
/// See <c>docs/business-rules/06-providers.md</c> <c>PROV-028</c>.
/// </summary>
/// <remarks>
/// Routed by URL rather than queued, because up to six files render at once and their request order is
/// genuinely unspecified — asserting one would be asserting a coincidence. What is asserted instead is
/// the request <em>count</em>, which is a fact, and the assembled text.
/// </remarks>
public sealed class AzureDiffTests
{
    private const string Pat = "ado-test-pat";

    private const string Org = "contoso";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A handler with the iteration lookup already answered, since every diff starts there.</summary>
    private static FakeHttpHandler Handler(string changes) =>
        new FakeHttpHandler()
            .When("/iterations?", """{"value":[{"id":3}]}""")
            .When("/changes?", changes);

    private static string Change(string path, string changeType, string? oldId, string? newId) =>
        $$"""
        {
          "changeType": "{{changeType}}",
          "item": {
            "path": "{{path}}",
            {{(oldId is null ? "" : $"\"originalObjectId\": \"{oldId}\",")}}
            {{(newId is null ? "" : $"\"objectId\": \"{newId}\",")}}
            "isFolder": false
          }
        }
        """;

    private static string Changes(params string[] entries) =>
        $$"""{"changeEntries":[{{string.Join(",", entries)}}]}""";

    /// <summary>A directory entry, which the assembly skips before it looks at either side.</summary>
    private static string Folder(string path, string objectId) =>
        $$"""{"changeType":"add","item":{"path":"{{path}}","isFolder":true,"objectId":"{{objectId}}"} }""";

    // ---------- the happy path ----------

    [Fact]
    public async Task A_modified_file_renders_as_a_unified_diff_naming_its_real_path()
    {
        using var handler = Handler(Changes(Change("/src/app.ts", "edit", "old-sha", "new-sha")))
            .WhenBytes("/blobs/old-sha", Encoding.UTF8.GetBytes("linea uno\nlinea dos\n"))
            .WhenBytes("/blobs/new-sha", Encoding.UTF8.GetBytes("linea uno\nlinea DOS\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        // The repo-absolute path Azure reports loses its leading slash: a finding cites a repo-relative
        // path, and so should a diff header.
        Assert.Contains("a/src/app.ts", diff, StringComparison.Ordinal);
        Assert.Contains("b/src/app.ts", diff, StringComparison.Ordinal);
        Assert.Contains("-linea dos", diff, StringComparison.Ordinal);
        Assert.Contains("+linea DOS", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_change_list_is_read_against_the_base_of_the_whole_pull_request()
    {
        using var handler = Handler(Changes(Change("/a.txt", "edit", "o", "n")))
            .WhenBytes("/blobs/o", Encoding.UTF8.GetBytes("a\n"))
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("b\n"));
        using var http = handler.Client();

        await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        var changes = handler.Requests.Single(r => r.Uri.ToString().Contains("/changes?", StringComparison.Ordinal));

        // No $compareTo, so this measures the whole pull request rather than only the last push.
        Assert.DoesNotContain("$compareTo", changes.Uri.ToString(), StringComparison.Ordinal);
        Assert.Contains("$top=1000", changes.Uri.ToString(), StringComparison.Ordinal);
        Assert.Contains("/iterations/3/changes", changes.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blob_is_read_as_an_octet_stream_because_a_file_is_not_text()
    {
        using var handler = Handler(Changes(Change("/a.txt", "edit", "o", "n")))
            .WhenBytes("/blobs/o", Encoding.UTF8.GetBytes("a\n"))
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("b\n"));
        using var http = handler.Client();

        await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        var blob = handler.Requests.First(r => r.Uri.ToString().Contains("/blobs/", StringComparison.Ordinal));
        Assert.Equal("application/octet-stream", blob.Header("Accept"));
    }

    [Fact]
    public async Task A_file_reads_its_base_side_before_its_target_side()
    {
        using var handler = Handler(Changes(Change("/a.txt", "edit", "old-sha", "new-sha")))
            .WhenBytes("/blobs/old-sha", Encoding.UTF8.GetBytes("a\n"))
            .WhenBytes("/blobs/new-sha", Encoding.UTF8.GetBytes("b\n"));
        using var http = handler.Client();

        await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        // Within one file the two reads are sequential, not concurrent — the doc says otherwise but
        // the implementation awaits them one after the other, and the source wins. Only distinct files overlap.
        var blobs = handler.Requests
            .Where(r => r.Uri.ToString().Contains("/blobs/", StringComparison.Ordinal))
            .Select(r => r.Uri.Segments[^1])
            .ToArray();

        Assert.Equal(["old-sha", "new-sha"], blobs);
    }

    // ---------- the sides that do not exist ----------

    [Fact]
    public async Task An_added_file_reads_only_its_target_side()
    {
        using var handler = Handler(Changes(Change("/nuevo.txt", "add", "irrelevant", "new-sha")))
            .WhenBytes("/blobs/new-sha", Encoding.UTF8.GetBytes("hola\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        // The change type says "add", so the base side is dropped even though Azure reported an id for it.
        Assert.Equal(0, handler.CountFor("/blobs/irrelevant"));
        Assert.Equal(1, handler.CountFor("/blobs/new-sha"));
        Assert.Contains("+hola", diff, StringComparison.Ordinal);

        // And it renders as a modification of an empty file, never as an addition — see UnifiedPatchTests.
        Assert.DoesNotContain("/dev/null", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deleted_file_reads_only_its_base_side()
    {
        using var handler = Handler(Changes(Change("/viejo.txt", "delete", "old-sha", "irrelevant")))
            .WhenBytes("/blobs/old-sha", Encoding.UTF8.GetBytes("adios\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Equal(0, handler.CountFor("/blobs/irrelevant"));
        Assert.Contains("-adios", diff, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000")]
    [InlineData("")]
    public async Task A_placeholder_object_id_counts_as_no_blob_at_all(string objectId)
    {
        using var handler = Handler(Changes(Change("/nuevo.txt", "edit", objectId, "new-sha")))
            .WhenBytes("/blobs/new-sha", Encoding.UTF8.GetBytes("hola\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        // Azure reports the all-zero id, and an empty string, for a side that does not exist. Fetching
        // either would be a request for a blob that is not there.
        Assert.Equal(1, handler.CountFor("/blobs/"));
        Assert.Contains("+hola", diff, StringComparison.Ordinal);
    }

    // ---------- what is skipped ----------

    [Fact]
    public async Task A_folder_entry_is_skipped_entirely()
    {
        using var handler = Handler(Changes(
                Folder("/src", "tree-sha"),
                Change("/src/a.txt", "edit", "o", "n")))
            .WhenBytes("/blobs/o", Encoding.UTF8.GetBytes("a\n"))
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("b\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Equal(0, handler.CountFor("/blobs/tree-sha"));
        Assert.Contains("a/src/a.txt", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_with_no_item_is_skipped_rather_than_failing()
    {
        using var handler = Handler(
            $$"""
            {"changeEntries":[
              {"changeType":"edit"},
              {{Change("/a.txt", "edit", "o", "n")}}
            ]}
            """)
            .WhenBytes("/blobs/o", Encoding.UTF8.GetBytes("a\n"))
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("b\n"));
        using var http = handler.Client();

        Assert.Contains(
            "a/a.txt",
            await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct),
            StringComparison.Ordinal);
    }

    // ---------- the placeholders ----------

    [Fact]
    public async Task A_file_whose_blob_cannot_be_read_is_listed_as_unreadable_and_the_rest_stands()
    {
        using var handler = Handler(Changes(
                Change("/broken.txt", "edit", "gone", "also-gone"),
                Change("/fine.txt", "edit", "o", "n")))
            .When("/blobs/gone", "", System.Net.HttpStatusCode.NotFound)
            .WhenBytes("/blobs/o", Encoding.UTF8.GetBytes("a\n"))
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("b\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        // One unreadable file does not fail a review that has already been fetched.
        Assert.Contains(
            "diff --git a/broken.txt b/broken.txt\n(couldn't read this file from Azure DevOps)\n",
            diff,
            StringComparison.Ordinal);
        Assert.Contains("a/fine.txt", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversized_blob_is_listed_and_downloaded_anyway()
    {
        var huge = new byte[(512 * 1024) + 1];
        using var handler = Handler(Changes(Change("/big.bin", "edit", "old-sha", "new-sha")))
            .WhenBytes("/blobs/old-sha", Encoding.UTF8.GetBytes("small\n"))
            .WhenBytes("/blobs/new-sha", huge);
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Equal(
            "diff --git a/big.bin b/big.bin\n(edit, too large to display)\n", diff);

        // The size is checked after both reads, because nothing in the change list reports it. So the
        // bytes are paid for and then thrown away — a real cost, reproduced rather than optimised away.
        Assert.Equal(1, handler.CountFor("/blobs/new-sha"));
        Assert.Equal(1, handler.CountFor("/blobs/old-sha"));
    }

    [Fact]
    public async Task The_change_type_is_quoted_back_in_the_placeholder_in_lower_case()
    {
        var huge = new byte[(512 * 1024) + 1];
        using var handler = Handler(Changes(Change("/big.bin", "Edit, SourceRename", "o", "n")))
            .WhenBytes("/blobs/o", huge)
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("x\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Contains("(edit, sourcerename, too large to display)", diff, StringComparison.Ordinal);
    }

    // ---------- truncation ----------

    [Fact]
    public async Task Past_eighty_files_the_rest_are_dropped_and_the_count_is_stated()
    {
        var entries = Enumerable.Range(0, 81)
            .Select(i => Change(
                string.Create(CultureInfo.InvariantCulture, $"/f{i}.txt"), "edit", "o", "n"))
            .ToArray();

        using var handler = Handler(Changes(entries))
            .WhenBytes("/blobs/o", Encoding.UTF8.GetBytes("a\n"))
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("b\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Contains("a/f79.txt", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("a/f80.txt", diff, StringComparison.Ordinal);
        Assert.EndsWith(
            "\n(only the first 80 of 81 changed files are included)\n", diff, StringComparison.Ordinal);

        // Eighty files, two reads each, and nothing fetched for the file that was dropped.
        Assert.Equal(160, handler.CountFor("/blobs/"));
    }

    [Fact]
    public async Task Exactly_eighty_files_carry_no_note()
    {
        var entries = Enumerable.Range(0, 80)
            .Select(i => Change(
                string.Create(CultureInfo.InvariantCulture, $"/f{i}.txt"), "edit", "o", "n"))
            .ToArray();

        using var handler = Handler(Changes(entries))
            .WhenBytes("/blobs/o", Encoding.UTF8.GetBytes("a\n"))
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("b\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.DoesNotContain("only the first", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_keep_the_order_azure_listed_them_in_however_they_finish()
    {
        var entries = Enumerable.Range(0, 12)
            .Select(i => Change(
                string.Create(CultureInfo.InvariantCulture, $"/f{i:D2}.txt"), "edit", "o", "n"))
            .ToArray();

        using var handler = Handler(Changes(entries))
            .WhenBytes("/blobs/o", Encoding.UTF8.GetBytes("a\n"))
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("b\n"));
        using var http = handler.Client();

        var diff = await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        // Six render at once, so completion order is not list order. A diff is read by a person and by a
        // model, and one whose files shuffled run to run would be a diff that changed without the code
        // changing.
        var order = Enumerable.Range(0, 12)
            .Select(i => diff.IndexOf(
                string.Create(CultureInfo.InvariantCulture, $"a/f{i:D2}.txt"), StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(position => position), order);
    }

    // ---------- nothing to review ----------

    [Fact]
    public async Task A_pull_request_with_no_changes_at_all_is_an_error_rather_than_an_empty_diff()
    {
        using var handler = Handler("""{"changeEntries":[]}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<AzureException>(
            () => AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct));

        Assert.Equal("This pull request has no file changes to review", failure.Message);
    }

    [Fact]
    public async Task A_change_list_whose_files_all_render_to_nothing_is_the_same_error()
    {
        // Every file identical on both sides: libgit2 renders no patch, so the assembled diff is blank.
        using var handler = Handler(Changes(Change("/same.txt", "edit", "o", "n")))
            .WhenBytes("/blobs/o", Encoding.UTF8.GetBytes("unchanged\n"))
            .WhenBytes("/blobs/n", Encoding.UTF8.GetBytes("unchanged\n"));
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<AzureException>(
            () => AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct));

        Assert.Equal("This pull request has no file changes to review", failure.Message);
    }

    [Fact]
    public async Task An_unreadable_file_is_still_a_diff_because_the_placeholder_is_content()
    {
        using var handler = Handler(Changes(Change("/broken.txt", "edit", "gone", "n")))
            .When("/blobs/gone", "", System.Net.HttpStatusCode.NotFound);
        using var http = handler.Client();

        // CodeFlow 1.7.2 checks whether the *assembled text* is blank, not whether anything rendered, so a
        // pull request whose every file failed to read still returns rather than erroring.
        Assert.Contains(
            "couldn't read this file from Azure DevOps",
            await AzureClient.PullRequestDiffAsync(http, Org, "Web", "Widget", 7, Pat, Ct),
            StringComparison.Ordinal);
    }
}
