using Xunit;

namespace CodeFlow.Tests.TestVectors;

/// <summary>
/// Guards the bridge between The extraction pass and this phase.
/// </summary>
/// <remarks>
/// The extraction pass's whole argument for extracting the extracted cases as data was that materialising them as
/// xUnit theories would be near-mechanical. These tests hold that claim to account: if a fixture
/// stops being loadable, or loses its cases, or names a unit nobody can find, the claim is no
/// longer true and this fails before a feature slice quietly builds on a broken assumption.
/// </remarks>
public sealed class FixtureCatalogTests
{
    /// <summary>The only two kinds <c>test-vectors/README.md</c> defines for extracted fixtures.</summary>
    private static readonly string[] ValidKinds = ["vector", "scenario"];

    public static TheoryData<string> FixtureFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in FixtureCatalog.Files())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Fact]
    public void The_catalog_directory_is_found_and_populated()
    {
        Assert.True(Directory.Exists(FixtureCatalog.Directory), FixtureCatalog.Directory);

        // The extraction pass produced 24 fixture files. A drop below that means fixtures were lost, which
        // matters because nothing else re-derives them.
        Assert.Equal(24, FixtureCatalog.Files().Count());
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Every_fixture_declares_the_schema_and_carries_cases(string fileName)
    {
        foreach (var fixture in FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, fileName)))
        {
            Assert.Equal("codeflow-fixture-v1", fixture.Schema);
            Assert.False(string.IsNullOrWhiteSpace(fixture.SourceFile), $"{fileName}: sourceFile is required");
            Assert.NotEmpty(fixture.ExtractedFrom);
            Assert.NotEmpty(fixture.Cases);
            Assert.Contains(fixture.Kind, ValidKinds);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Scenario_fixtures_carry_their_seed_artefact(string fileName)
    {
        foreach (var fixture in FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, fileName)))
        {
            // Seeds appear at either level — see FixtureCase.Setup for why. Checking only the
            // fixture level silently skipped every fixture that used the per-case form.
            var seeds = new[] { fixture.Setup?.SeedSql }
                .Concat(fixture.Cases.Select(c => c.Setup?.SeedSql))
                .OfType<string>();

            foreach (var seed in seeds)
            {
                // A scenario fixture whose seed is missing is incomplete, and The extraction pass flagged
                // that as the part of the extraction most dependent on judgement rather than a
                // mechanical check — so it gets a mechanical check here.
                var path = Path.Combine(FixtureCatalog.Directory, seed);
                Assert.True(File.Exists(path), $"{fileName} references a missing seed: {seed}");
                Assert.NotEmpty(File.ReadAllText(path));
            }
        }
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Every_case_is_identifiable(string fileName)
    {
        foreach (var fixture in FixtureCatalog.Load(Path.Combine(FixtureCatalog.Directory, fileName)))
        {
            foreach (var testCase in fixture.Cases)
            {
                // Theories report by case id; an unnamed case is unreportable when it fails.
                Assert.False(
                    string.IsNullOrWhiteSpace(testCase.Id) && string.IsNullOrWhiteSpace(testCase.Name),
                    $"{fileName} has a case with neither id nor name");
            }
        }
    }

    [Fact]
    public void Every_case_group_named_by_a_fixture_is_unique_to_it()
    {
        // Two fixtures claiming the same extracted case means one of them is a stale copy, which is
        // exactly how an extracted vector silently stops matching its source.
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in FixtureCatalog.Files())
        {
            var name = Path.GetFileName(file);
            foreach (var fixture in FixtureCatalog.Load(file))
            {
                foreach (var caseGroup in fixture.ExtractedFrom)
                {
                    // The six AI engines mirror one another's error/quota contract deliberately,
                    // so four test names legitimately recur across engine files. Key on the pair.
                    var key = $"{fixture.SourceFile}::{caseGroup}";
                    Assert.False(
                        claims.TryGetValue(key, out var owner),
                        $"{key} is claimed by both {owner} and {name}");
                    claims[key] = name;
                }
            }
        }

        Assert.NotEmpty(claims);
    }
}
