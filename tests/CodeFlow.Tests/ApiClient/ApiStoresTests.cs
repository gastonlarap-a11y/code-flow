using CodeFlow.ApiClient;
using CodeFlow.Storage;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.ApiClient;

/// <summary>
/// Environments, history and the cookie jar. See <c>docs/business-rules/03-storage.md</c>.
/// </summary>
public sealed class ApiStoresTests
{
    // ---------- environments ----------

    [Fact]
    public void Globals_sorts_ahead_of_every_environment_added_later()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);
        database.Do(Migrations.Run);

        database.Do(c => ApiEnvironmentStore.Create(c, workspace, "Staging"));
        database.Do(c => ApiEnvironmentStore.Create(c, workspace, "Production"));

        Assert.Equal(
            ["Globals", "Staging", "Production"],
            database.Use(c => ApiEnvironmentStore.List(c, workspace)).Select(e => e.Name));
    }

    /// <summary>Which row is Globals is the database's business, not a client round trip's.</summary>
    [Fact]
    public void An_update_cannot_make_an_environment_global()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);
        var environment = database.Use(c => ApiEnvironmentStore.Create(c, workspace, "Staging"));

        database.Do(c => ApiEnvironmentStore.Update(c, environment with { IsGlobal = true, Variables = "[1]" }));

        var stored = Assert.Single(database.Use(c => ApiEnvironmentStore.List(c, workspace)));
        Assert.False(stored.IsGlobal);
        Assert.Equal("[1]", stored.Variables);
    }

    /// <summary>Globals is always in scope, and there is no UI to recreate it.</summary>
    [Fact]
    public void Deleting_globals_does_nothing()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);
        database.Do(Migrations.Run);
        var globals = Assert.Single(database.Use(c => ApiEnvironmentStore.List(c, workspace)));

        database.Do(c => ApiEnvironmentStore.Delete(c, globals.Id));

        Assert.Single(database.Use(c => ApiEnvironmentStore.List(c, workspace)));
    }

    [Fact]
    public void An_ordinary_environment_can_be_deleted()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);
        var environment = database.Use(c => ApiEnvironmentStore.Create(c, workspace, "Staging"));

        database.Do(c => ApiEnvironmentStore.Delete(c, environment.Id));

        Assert.Empty(database.Use(c => ApiEnvironmentStore.List(c, workspace)));
    }

    /// <summary>
    /// Duplicating Globals yields an ordinary environment.
    /// </summary>
    /// <remarks>
    /// Its variables are a reasonable starting point for one, and the copy must not become a second
    /// row claiming to be the workspace's globals.
    /// </remarks>
    [Fact]
    public void Duplicating_globals_yields_an_ordinary_environment()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);
        database.Do(Migrations.Run);
        var globals = Assert.Single(database.Use(c => ApiEnvironmentStore.List(c, workspace)));

        var copy = database.Use(c => ApiEnvironmentStore.Duplicate(c, globals.Id));

        Assert.Equal("Globals copy", copy.Name);
        Assert.False(copy.IsGlobal);
        Assert.Equal(workspace, copy.WorkspaceId);
    }

    // ---------- history ----------

    [Fact]
    public void History_comes_back_newest_first_and_honours_the_limit()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);

        foreach (var (id, at) in new[] { ("a", "2026-01-01"), ("b", "2026-01-02"), ("c", "2026-01-03") })
        {
            database.Do(c => ApiHistoryStore.Add(c, Entry(id, workspace, at)));
        }

        Assert.Equal(["c", "b", "a"], database.Use(c => ApiHistoryStore.List(c, workspace, 10)).Select(e => e.Id));
        Assert.Equal(["c"], database.Use(c => ApiHistoryStore.List(c, workspace, 1)).Select(e => e.Id));
    }

    /// <summary>The frontend mints the id, so a retry must not double the row.</summary>
    [Fact]
    public void Adding_the_same_entry_twice_keeps_one_row()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);

        database.Do(c => ApiHistoryStore.Add(c, Entry("a", workspace, "2026-01-01")));
        database.Do(c => ApiHistoryStore.Add(c, Entry("a", workspace, "2026-01-02")));

        var stored = Assert.Single(database.Use(c => ApiHistoryStore.List(c, workspace, 10)));
        Assert.Equal("2026-01-01", stored.CreatedAt);
    }

    [Fact]
    public void An_entry_with_no_timestamp_is_stamped_on_the_way_in()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);

        database.Do(c => ApiHistoryStore.Add(c, Entry("a", workspace, "  ")));

        Assert.NotEqual("  ", Assert.Single(database.Use(c => ApiHistoryStore.List(c, workspace, 10))).CreatedAt);
    }

    [Fact]
    public void Clearing_history_leaves_another_workspaces_alone()
    {
        using var database = new TempDatabase();
        var mine = Workspace(database);
        var other = database.Use(c => WorkspaceStore.Create(c, "Other", "folder", "#000")).Id;

        database.Do(c => ApiHistoryStore.Add(c, Entry("a", mine, "2026-01-01")));
        database.Do(c => ApiHistoryStore.Add(c, Entry("b", other, "2026-01-01")));

        database.Do(c => ApiHistoryStore.Clear(c, mine));

        Assert.Empty(database.Use(c => ApiHistoryStore.List(c, mine, 10)));
        Assert.Single(database.Use(c => ApiHistoryStore.List(c, other, 10)));
    }

    // ---------- cookies ----------

    /// <summary>
    /// <c>STORE-020</c>: a cookie's identity is its domain, path and name — not its row id.
    /// </summary>
    /// <remarks>
    /// A <c>Set-Cookie</c> for one the jar already holds has to replace it. Keying on the id
    /// instead would fill the jar with stale duplicates and leave the request builder guessing
    /// which is current.
    /// </remarks>
    [Fact]
    public void The_same_cookie_sent_again_replaces_it_rather_than_accumulating()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);

        database.Do(c => ApiCookieStore.Upsert(c, Cookie("first", workspace, "example.test", "/", "session", "one")));
        database.Do(c => ApiCookieStore.Upsert(c, Cookie("second", workspace, "example.test", "/", "session", "two")));

        var stored = Assert.Single(database.Use(c => ApiCookieStore.List(c, workspace)));
        Assert.Equal("two", stored.Value);

        // The row kept its original id: the conflict updated in place rather than inserting.
        Assert.Equal("first", stored.Id);
    }

    [Theory]
    [InlineData("other.test", "/", "session")]
    [InlineData("example.test", "/admin", "session")]
    [InlineData("example.test", "/", "csrf")]
    public void A_cookie_differing_in_any_part_of_its_key_is_a_different_cookie(
        string domain, string path, string name)
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);

        database.Do(c => ApiCookieStore.Upsert(c, Cookie("a", workspace, "example.test", "/", "session", "one")));
        database.Do(c => ApiCookieStore.Upsert(c, Cookie("b", workspace, domain, path, name, "two")));

        Assert.Equal(2, database.Use(c => ApiCookieStore.List(c, workspace)).Count);
    }

    /// <summary>A staging session in one workspace never overwrites the same host's in another.</summary>
    [Fact]
    public void The_same_cookie_in_two_workspaces_stays_two_cookies()
    {
        using var database = new TempDatabase();
        var mine = Workspace(database);
        var other = database.Use(c => WorkspaceStore.Create(c, "Other", "folder", "#000")).Id;

        database.Do(c => ApiCookieStore.Upsert(c, Cookie("a", mine, "example.test", "/", "session", "mine")));
        database.Do(c => ApiCookieStore.Upsert(c, Cookie("b", other, "example.test", "/", "session", "theirs")));

        Assert.Equal("mine", Assert.Single(database.Use(c => ApiCookieStore.List(c, mine))).Value);
        Assert.Equal("theirs", Assert.Single(database.Use(c => ApiCookieStore.List(c, other))).Value);
    }

    [Fact]
    public void Cookies_are_listed_by_domain_then_path_then_name()
    {
        using var database = new TempDatabase();
        var workspace = Workspace(database);

        database.Do(c => ApiCookieStore.Upsert(c, Cookie("a", workspace, "b.test", "/", "z", "1")));
        database.Do(c => ApiCookieStore.Upsert(c, Cookie("b", workspace, "a.test", "/x", "a", "1")));
        database.Do(c => ApiCookieStore.Upsert(c, Cookie("c", workspace, "a.test", "/", "b", "1")));

        Assert.Equal(["c", "b", "a"], database.Use(c => ApiCookieStore.List(c, workspace)).Select(x => x.Id));
    }

    private static string Workspace(TempDatabase database) =>
        database.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;

    private static ApiHistoryEntry Entry(string id, string workspaceId, string createdAt) =>
        new(id, workspaceId, null, "Login", "http", "POST", "https://example.test", 200, 12, 34, "{}", createdAt);

    private static ApiCookie Cookie(
        string id, string workspaceId, string domain, string path, string name, string value) =>
        new(id, workspaceId, domain, path, name, value, false, false, null, "2026-01-01");
}
