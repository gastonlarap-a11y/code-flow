using System.Text.Json;
using CodeFlow.Providers;
using CodeFlow.Review;
using CodeFlow.Tests.Providers;
using CodeFlow.Tests.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// Publishing the findings of a review reached by link alone.
/// See <c>docs/business-rules/07-review-pipeline.md</c> <c>REVIEW-013</c>.
/// </summary>
/// <remarks>
/// The counterpart with no memory: there is no saved run, so nothing reconciles and nothing is
/// written back. <c>UNVERIFIED</c> against a real API, and offline by construction.
/// </remarks>
[Collection(SerialKeychain.Name)]
public sealed class ReviewPostingFromLinkTests
{
    private const string Org = "codeflow-link-posting";
    private const string Url = "https://dev.azure.com/codeflow-link-posting/Web/_git/Widget/pullrequest/7";

    [Fact]
    public async Task Every_finding_opens_a_fresh_thread_every_time()
    {
        using var fixture = new Fixture();

        await fixture.PublishAsync(Item("src/a.ts", "uno"), Item("src/b.ts", "dos"));
        await fixture.PublishAsync(Item("src/a.ts", "uno"));

        // Three writes for three items across two posts: nothing here remembers that the first two
        // already have threads, because there is nowhere to remember it.
        Assert.Equal(3, fixture.Writes.Count);
        Assert.All(fixture.Writes, request =>
            Assert.EndsWith("/threads?api-version=7.1", request.Uri.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_finding_with_no_location_still_posts_as_a_plain_comment()
    {
        using var fixture = new Fixture();

        await fixture.PublishAsync(new PostFindingItem(null, "Estilo", "sin ubicación", Location: null));

        var body = JsonDocument.Parse(fixture.Writes[0].Body!).RootElement;
        Assert.False(body.TryGetProperty("threadContext", out _));
        Assert.Equal(
            "sin ubicación",
            body.GetProperty("comments").EnumerateArray().Single().GetProperty("content").GetString());
    }

    [Fact]
    public async Task The_summary_rides_along_as_its_own_comment()
    {
        using var fixture = new Fixture();

        await fixture.PublishAsync(postSummary: true, summary: "resumen", Item("src/a.ts", "uno"));

        // First, like the project-linked path: a summary under its own findings reads as a
        // postscript to them.
        Assert.Equal(2, fixture.Writes.Count);
        Assert.Equal(
            "resumen",
            JsonDocument.Parse(fixture.Writes[0].Body!).RootElement
                .GetProperty("comments").EnumerateArray().Single().GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_link_nothing_recognises_fails_before_any_network_call()
    {
        using var fixture = new Fixture();

        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            fixture.PublishAsync("https://example.com/not-a-pull-request", Item("src/a.ts", "uno")));

        Assert.Equal("That isn't a pull-request link CodeFlow can read", failure.Message);
        Assert.Empty(fixture.Writes);
    }

    private static PostFindingItem Item(string file, string content) =>
        new(file, "Seguridad", content, new CommentLocation(file, 12, 12));

    private sealed class Fixture : IDisposable
    {
        private readonly TempAdoPat _pat;
        private readonly FakeHttpHandler _handler = new();
        private readonly HttpClient _http;
        private readonly TempDatabase _db = new();

        public Fixture()
        {
            _pat = new TempAdoPat(Org);
            _http = _handler.Client();
            _handler.When("/iterations?", """{"value":[{"id":3}]}""");
            _handler.When("/threads", """{"id":501}""");
        }

        /// <summary>The writes, with the iteration lookups an anchored post makes filtered out.</summary>
        public IReadOnlyList<FakeHttpHandler.Captured> Writes =>
            [.. _handler.Requests.Where(r => !r.Uri.ToString().Contains("/iterations?", StringComparison.Ordinal))];

        public Task PublishAsync(params PostFindingItem[] items) =>
            PublishAsync(Url, postSummary: false, summary: null, items);

        public Task PublishAsync(string url, params PostFindingItem[] items) =>
            PublishAsync(url, postSummary: false, summary: null, items);

        public Task PublishAsync(bool postSummary, string? summary, params PostFindingItem[] items) =>
            PublishAsync(Url, postSummary, summary, items);

        public Task PublishAsync(string url, bool postSummary, string? summary, PostFindingItem[] items) =>
            ReviewPosting.PublishFromLinkAsync(
                _db.Handle, _http, url, items, postSummary, summary, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _db.Dispose();
            _http.Dispose();
            _handler.Dispose();
            _pat.Dispose();
        }
    }
}
