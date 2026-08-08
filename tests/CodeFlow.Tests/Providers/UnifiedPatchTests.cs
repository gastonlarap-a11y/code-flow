using System.Text;
using System.Text.Json;
using CodeFlow.Providers.Azure;
using CodeFlow.Tests.TestVectors;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// Rendering one file's two blobs as a unified diff, the way Azure's diff assembly needs.
/// </summary>
/// <remarks>
/// Driven by <c>ado.vectors.json</c>, extracted in The extraction pass from 1.7.2's own
/// <c>unified_patch_renders_a_git_style_diff</c> and <c>unified_patch_handles_added_and_deleted_files</c>.
/// The assertions are substring containment, not equality, because the extracted cases assert
/// <c>patch.contains(…)</c> and the fixture's notes ask for the same rather than pinning a byte-identical
/// string against a renderer that has not been proven byte-identical.
/// </remarks>
[Collection(SerialTemporaryFiles.Name)]
public sealed class UnifiedPatchTests
{
    private const string Vectors = "ado.vectors.json";

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_extracted_vectors_render_as_the_reference_renders_them(string caseId)
    {
        var testCase = Find(caseId);
        var path = testCase.Input.GetProperty("path").GetString()!;
        var before = Bytes(testCase.Input, "old");
        var after = Bytes(testCase.Input, "new");

        var patch = UnifiedPatch.Render(path, before, after);

        if (testCase.Expected.GetProperty("isSome").GetBoolean())
        {
            Assert.NotNull(patch);
        }
        else
        {
            Assert.Null(patch);
            return;
        }

        foreach (var fragment in testCase.Expected.GetProperty("containsAll").EnumerateArray())
        {
            Assert.Contains(fragment.GetString()!, patch, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The assertion the vectors do not make, and the one thing this approach could get wrong.
    /// </summary>
    /// <remarks>
    /// The diff is handed two present buffers on every call — an empty buffer is
    /// still a buffer — so it can never produce an <c>Added</c> or <c>Deleted</c> delta, and never emits
    /// <c>new file mode</c> or <c>/dev/null</c>. Omitting the path from one tree instead of pointing it at
    /// an empty blob would make libgit2 detect the addition and emit both. This test is what stops that
    /// regression, since the containment vectors above would still pass with the wrong header present.
    /// </remarks>
    [Theory]
    [InlineData("", "hola\n")]
    [InlineData("adios\n", "")]
    public void A_side_that_does_not_exist_still_renders_as_a_modification(string before, string after)
    {
        var patch = UnifiedPatch.Render("nuevo.txt", Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));

        Assert.NotNull(patch);
        Assert.DoesNotContain("new file mode", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("deleted file mode", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/null", patch, StringComparison.Ordinal);

        // Both real paths, on both sides — which is the whole reason trees are used instead of blobs.
        Assert.Contains("a/nuevo.txt", patch, StringComparison.Ordinal);
        Assert.Contains("b/nuevo.txt", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_did_not_change_renders_empty_rather_than_null()
    {
        // The distinction matters: null means "libgit2 could not render this", which the caller reports as
        // binary. Two identical sides are not that — 1.7.2 returns an empty string, its caller
        // appends nothing, and a pull request whose every file rendered empty is what produces "no file
        // changes to review".
        var content = Encoding.UTF8.GetBytes("unchanged\n");

        Assert.Equal(string.Empty, UnifiedPatch.Render("same.txt", content, content));
    }

    [Fact]
    public void A_path_with_a_space_keeps_it_in_the_header()
    {
        // Azure project and file names routinely carry spaces, and the header is what a finding cites.
        var patch = UnifiedPatch.Render(
            "src/my file.ts", Encoding.UTF8.GetBytes("uno\n"), Encoding.UTF8.GetBytes("dos\n"));

        Assert.NotNull(patch);
        Assert.Contains("a/src/my file.ts", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_temporary_repository_does_not_outlive_the_call()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "codeflow-ado-diff-*").Length;

        UnifiedPatch.Render("x.txt", Encoding.UTF8.GetBytes("a\n"), Encoding.UTF8.GetBytes("b\n"));

        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "codeflow-ado-diff-*").Length);
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var testCase in Load())
        {
            data.Add(testCase.Id!);
        }

        Assert.NotEmpty(data);
        return data;
    }

    private static IEnumerable<FixtureCase> Load() =>
        FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, Vectors)).SelectMany(f => f.Cases);

    private static FixtureCase Find(string caseId) => Load().Single(c => c.Id == caseId);

    /// <summary>
    /// A blob side, given in the fixture as text because both vector sides are UTF-8.
    /// </summary>
    private static byte[] Bytes(JsonElement input, string property) =>
        Encoding.UTF8.GetBytes(input.GetProperty(property).GetString()!);
}
