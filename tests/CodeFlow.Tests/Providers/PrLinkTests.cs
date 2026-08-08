using System.Text.Json;
using CodeFlow.Providers;
using CodeFlow.Tests.TestVectors;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// The pasted-pull-request-link parser, driven by the vectors The extraction pass extracted from
/// the extracted cases.
/// </summary>
/// <remarks>
/// These are 1.7.2's assertions replayed against this codebase, not a fresh opinion about URL
/// grammar — which matters here more than usual, because the shapes come from what the Azure portal
/// and GitHub actually emit rather than from a spec anyone could re-derive.
/// </remarks>
public sealed class PrLinkTests
{
    private const string VectorFile = "pr_link.vectors.json";

    public static TheoryData<string> ParseCases()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in Load())
        {
            foreach (var testCase in fixture.Cases)
            {
                data.Add(testCase.Id!);
            }
        }

        // Eleven cases at the time of writing; the assertion is that the file was found at all.
        Assert.NotEmpty(data);
        return data;
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Parse_matches_the_extracted_vector(string caseId)
    {
        var testCase = Load().SelectMany(f => f.Cases).Single(c => c.Id == caseId);

        var url = testCase.Input.GetProperty("url").GetString()!;
        var hosts = testCase.Input.GetProperty("knownGithubHosts")
            .EnumerateArray()
            .Select(host => host.GetString()!)
            .ToArray();

        var parsed = PrLink.Parse(url, hosts);

        // A vector whose expectation is JSON null is a link 1.7.2 refuses to recognise.
        if (testCase.Expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(parsed);
            return;
        }

        var expected = testCase.Expected;
        Assert.Equal(expected.GetProperty("number").GetInt64(), Number(parsed));

        switch (expected.GetProperty("type").GetString())
        {
            case "GitHub":
                var github = Assert.IsType<PrLinkTarget.GitHub>(parsed);
                Assert.Equal(expected.GetProperty("host").GetString(), github.Host);
                Assert.Equal(expected.GetProperty("owner").GetString(), github.Owner);
                Assert.Equal(expected.GetProperty("repo").GetString(), github.Repo);
                break;

            case "Azure":
                var azure = Assert.IsType<PrLinkTarget.Azure>(parsed);
                Assert.Equal(expected.GetProperty("org").GetString(), azure.Org);
                Assert.Equal(expected.GetProperty("project").GetString(), azure.Project);
                Assert.Equal(expected.GetProperty("repo").GetString(), azure.Repo);
                break;

            default:
                Assert.Fail($"the vector names a target type this test does not know: {expected}");
                break;
        }
    }

    [Fact]
    public void Every_vector_case_is_exercised()
    {
        // The theory above silently proves nothing if the fixture file moves or is renamed, so the
        // count is asserted independently — the same reason FixtureCatalogTests exists.
        Assert.Equal(11, Load().Sum(fixture => fixture.Cases.Length));
    }

    private static IReadOnlyList<Fixture> Load() =>
        FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, VectorFile));

    private static long Number(PrLinkTarget? target) => target switch
    {
        PrLinkTarget.GitHub github => github.Number,
        PrLinkTarget.Azure azure => azure.Number,
        _ => throw new InvalidOperationException("expected the link to parse"),
    };
}
