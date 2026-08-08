using CodeFlow.ApiClient;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeFlow.Tests.ApiClient;

/// <summary>
/// The API tester's tree: collections, folders, requests, and the move that reorders them.
/// See <c>docs/business-rules/03-storage.md</c>.
/// </summary>
/// <remarks>
/// <b>No vectors exist for any of this.</b> <c>03-storage.md</c> states that
/// the implementation has no extracted cases of its own and is exercised only indirectly
/// through the command layer, and <c>queries.vectors.json</c> belongs to an unrelated file. So the
/// specification here is the <c>STORE-0xx</c> rules in prose, and these tests are written against
/// them rather than ported.
/// </remarks>
public sealed class ApiTreeStoreTests
{
    [Fact]
    public void The_whole_tree_comes_back_in_one_call()
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");
        var folder = fixture.Folder(collection.Id, null, "Auth");
        var request = fixture.Request(collection.Id, folder.Id, "Login");

        var tree = fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId));

        Assert.Equal(collection.Id, Assert.Single(tree.Collections).Id);
        Assert.Equal(folder.Id, Assert.Single(tree.Folders).Id);
        Assert.Equal(request.Id, Assert.Single(tree.Requests).Id);
    }

    [Fact]
    public void The_tree_holds_only_this_workspaces_rows()
    {
        using var fixture = new Fixture();
        fixture.Collection("Mine");

        var other = fixture.Use(c => WorkspaceStore.Create(c, "Other", "folder", "#000")).Id;
        fixture.Use(c => ApiTreeStore.CreateCollection(c, other, "Theirs"));

        Assert.Equal("Mine", Assert.Single(fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId)).Collections).Name);
    }

    [Fact]
    public void Deleting_a_collection_takes_its_folders_and_requests_with_it()
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");
        var folder = fixture.Folder(collection.Id, null, "Auth");
        fixture.Request(collection.Id, folder.Id, "Login");

        fixture.Use(c => { ApiTreeStore.DeleteCollection(c, collection.Id); return 0; });

        var tree = fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId));
        Assert.Empty(tree.Collections);
        Assert.Empty(tree.Folders);
        Assert.Empty(tree.Requests);
    }

    /// <summary>
    /// A collection update writes the editable fields and leaves <c>sort_order</c> alone.
    /// </summary>
    /// <remarks>
    /// Order belongs to <c>api_reorder_collections</c>. Writing it back from a client that has not
    /// seen a concurrent reorder would scramble the sidebar, and the symptom would look like the
    /// drag failing rather than the save succeeding.
    /// </remarks>
    [Fact]
    public void Updating_a_collection_cannot_move_it()
    {
        using var fixture = new Fixture();
        var first = fixture.Collection("First");
        var second = fixture.Collection("Second");

        fixture.Use(c => { ApiTreeStore.UpdateCollection(c, second with { Name = "Renamed", SortOrder = 0 }); return 0; });

        var collections = fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId)).Collections;
        Assert.Equal([first.Name, "Renamed"], collections.Select(x => x.Name));
        Assert.Equal([0, 1], collections.Select(x => x.SortOrder));
    }

    [Fact]
    public void Reordering_renumbers_the_sidebar_top_to_bottom()
    {
        using var fixture = new Fixture();
        var first = fixture.Collection("First");
        var second = fixture.Collection("Second");
        var third = fixture.Collection("Third");

        fixture.Use(c =>
        {
            ApiTreeStore.ReorderCollections(c, fixture.WorkspaceId, [third.Id, first.Id, second.Id]);
            return 0;
        });

        Assert.Equal(
            ["Third", "First", "Second"],
            fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId)).Collections.Select(x => x.Name));
    }

    /// <summary>A list from a workspace the user just left renumbers nothing.</summary>
    [Fact]
    public void Reordering_with_the_wrong_workspace_changes_nothing()
    {
        using var fixture = new Fixture();
        var first = fixture.Collection("First");
        var second = fixture.Collection("Second");
        var other = fixture.Use(c => WorkspaceStore.Create(c, "Other", "folder", "#000")).Id;

        fixture.Use(c => { ApiTreeStore.ReorderCollections(c, other, [second.Id, first.Id]); return 0; });

        Assert.Equal(
            ["First", "Second"],
            fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId)).Collections.Select(x => x.Name));
    }

    /// <summary>A duplicate shares no row with its source, so the two can diverge freely.</summary>
    [Fact]
    public void Duplicating_a_collection_deep_copies_it_and_remaps_the_parent_links()
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");
        var parent = fixture.Folder(collection.Id, null, "Auth");
        var child = fixture.Folder(collection.Id, parent.Id, "OAuth");
        fixture.Request(collection.Id, child.Id, "Token");

        var copy = fixture.Use(c => ApiTreeStore.DuplicateCollection(c, collection.Id));

        Assert.Equal("Requests copy", copy.Name);
        Assert.Equal(fixture.WorkspaceId, copy.WorkspaceId);

        var tree = fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId));
        var copiedFolders = tree.Folders.Where(f => f.CollectionId == copy.Id).ToList();
        var copiedRequest = Assert.Single(tree.Requests, r => r.CollectionId == copy.Id);

        Assert.Equal(2, copiedFolders.Count);
        Assert.DoesNotContain(copiedFolders, f => f.Id == parent.Id || f.Id == child.Id);

        // The nesting survived: the copied child points at the copied parent, not the original.
        var copiedParent = Assert.Single(copiedFolders, f => f.ParentId is null);
        var copiedChild = Assert.Single(copiedFolders, f => f.ParentId is not null);
        Assert.Equal(copiedParent.Id, copiedChild.ParentId);
        Assert.Equal(copiedChild.Id, copiedRequest.FolderId);
    }

    [Fact]
    public void A_new_request_takes_its_method_and_url_from_the_spec_it_was_given()
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");

        var request = fixture.Use(c => ApiTreeStore.CreateRequest(
            c, collection.Id, null, "Login", "http", """{"method":"POST","url":"https://example.test/login"}"""));

        // The two columns exist so the tree can render a row without opening the blob; leaving them
        // at the schema default would list a filled-in request as GET with no URL.
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://example.test/login", request.Url);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("""{"method":""}""")]
    public void A_spec_with_no_method_still_saves_and_falls_back_to_get(string spec)
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");

        var request = fixture.Use(c => ApiTreeStore.CreateRequest(c, collection.Id, null, "New", "http", spec));

        Assert.Equal("GET", request.Method);
        Assert.Equal(string.Empty, request.Url);
        Assert.Equal(spec, request.Spec);
    }

    // ---------- moving ----------

    [Fact]
    public void Moving_a_request_renumbers_its_new_siblings_densely()
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");
        var first = fixture.Request(collection.Id, null, "First");
        var second = fixture.Request(collection.Id, null, "Second");
        var third = fixture.Request(collection.Id, null, "Third");

        fixture.Use(c => { ApiTreeStore.MoveNode(c, "request", third.Id, collection.Id, null, 0); return 0; });

        var requests = fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId)).Requests;
        Assert.Equal(["Third", "First", "Second"], requests.Select(r => r.Name));
        Assert.Equal([0, 1, 2], requests.Select(r => r.SortOrder));
        Assert.Equal(first.Id, requests[1].Id);
        Assert.Equal(second.Id, requests[2].Id);
    }

    [Fact]
    public void An_index_past_the_end_lands_last_rather_than_failing()
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");
        fixture.Request(collection.Id, null, "First");
        var second = fixture.Request(collection.Id, null, "Second");

        fixture.Use(c => { ApiTreeStore.MoveNode(c, "request", second.Id, collection.Id, null, 99); return 0; });

        Assert.Equal(
            ["First", "Second"],
            fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId)).Requests.Select(r => r.Name));
    }

    /// <summary>A folder cannot swallow itself — that would detach the whole subtree.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_folder_cannot_be_moved_inside_itself_or_its_own_descendant(bool intoDescendant)
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");
        var parent = fixture.Folder(collection.Id, null, "Auth");
        var child = fixture.Folder(collection.Id, parent.Id, "OAuth");

        var destination = intoDescendant ? child.Id : parent.Id;

        var failure = Assert.Throws<InvalidOperationException>(() =>
            fixture.Use(c => { ApiTreeStore.MoveNode(c, "folder", parent.Id, collection.Id, destination, 0); return 0; }));

        Assert.Equal("A folder cannot be moved inside itself", failure.Message);
    }

    /// <summary>
    /// Nothing may cross into another workspace's collection.
    /// </summary>
    /// <remarks>
    /// It would vanish from the tree the user is looking at and reappear in one they cannot see
    /// from here — a loss with no visible cause. The UI guards it too; this is the guard that holds
    /// when the UI has a bug.
    /// </remarks>
    [Fact]
    public void A_node_cannot_be_moved_into_another_workspaces_collection()
    {
        using var fixture = new Fixture();
        var mine = fixture.Collection("Mine");
        var request = fixture.Request(mine.Id, null, "Login");

        var other = fixture.Use(c => WorkspaceStore.Create(c, "Other", "folder", "#000")).Id;
        var theirs = fixture.Use(c => ApiTreeStore.CreateCollection(c, other, "Theirs"));

        var failure = Assert.Throws<InvalidOperationException>(() =>
            fixture.Use(c => { ApiTreeStore.MoveNode(c, "request", request.Id, theirs.Id, null, 0); return 0; }));

        Assert.Equal("A node cannot be moved to a collection in another workspace", failure.Message);
    }

    /// <summary>
    /// Moving a folder to another collection carries its whole subtree with it.
    /// </summary>
    /// <remarks>
    /// Only the folder's own row is reparented by the move; its descendants still name the
    /// collection they came from, and the tree would then render them under a collection they are
    /// no longer reachable from.
    /// </remarks>
    [Fact]
    public void Moving_a_folder_between_collections_carries_everything_under_it()
    {
        using var fixture = new Fixture();
        var source = fixture.Collection("Source");
        var destination = fixture.Collection("Destination");
        var parent = fixture.Folder(source.Id, null, "Auth");
        var child = fixture.Folder(source.Id, parent.Id, "OAuth");
        var request = fixture.Request(source.Id, child.Id, "Token");

        fixture.Use(c => { ApiTreeStore.MoveNode(c, "folder", parent.Id, destination.Id, null, 0); return 0; });

        var tree = fixture.Use(c => ApiTreeStore.LoadTree(c, fixture.WorkspaceId));
        Assert.All(
            tree.Folders.Where(f => f.Id == parent.Id || f.Id == child.Id),
            f => Assert.Equal(destination.Id, f.CollectionId));
        Assert.Equal(destination.Id, Assert.Single(tree.Requests, r => r.Id == request.Id).CollectionId);

        // And the nesting below it is untouched.
        Assert.Equal(parent.Id, Assert.Single(tree.Folders, f => f.Id == child.Id).ParentId);
    }

    [Fact]
    public void An_unknown_node_kind_is_refused_by_name()
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            fixture.Use(c => { ApiTreeStore.MoveNode(c, "sausage", "x", collection.Id, null, 0); return 0; }));

        Assert.Equal("Unknown node kind sausage", failure.Message);
    }

    [Fact]
    public void Duplicating_a_request_lands_beside_its_source()
    {
        using var fixture = new Fixture();
        var collection = fixture.Collection("Requests");
        var folder = fixture.Folder(collection.Id, null, "Auth");
        var request = fixture.Request(collection.Id, folder.Id, "Login");

        var copy = fixture.Use(c => ApiTreeStore.DuplicateRequest(c, request.Id));

        Assert.Equal("Login copy", copy.Name);
        Assert.Equal(folder.Id, copy.FolderId);
        Assert.NotEqual(request.Id, copy.Id);
        Assert.Equal(request.SortOrder + 1, copy.SortOrder);
    }

    /// <summary>A workspace and one migrated database, wrapped so the tests read as prose.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase _database = new();

        public Fixture() =>
            WorkspaceId = Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#fff")).Id;

        public string WorkspaceId { get; }

        public T Use<T>(Func<SqliteConnection, T> work) => _database.Use(work);

        public ApiCollection Collection(string name) => Use(c => ApiTreeStore.CreateCollection(c, WorkspaceId, name));

        public ApiFolder Folder(string collectionId, string? parentId, string name) =>
            Use(c => ApiTreeStore.CreateFolder(c, collectionId, parentId, name));

        public ApiRequestRow Request(string collectionId, string? folderId, string name) =>
            Use(c => ApiTreeStore.CreateRequest(c, collectionId, folderId, name, "http", "{}"));

        public void Dispose() => _database.Dispose();
    }
}
