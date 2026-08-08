using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Tests.TestVectors;

/// <summary>
/// Reads the The extraction pass fixture files under <c>docs/business-rules/test-vectors/</c>.
/// </summary>
/// <remarks>
/// The extraction pass mined CodeFlow 1.7.2's 133 extracted case functions into data so this phase
/// could materialise them as xUnit theories without re-deriving them. This type is the single
/// place that knows where they live and what shape they have; feature tests take their
/// <c>[MemberData]</c> from here.
/// </remarks>
public static class FixtureCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The <c>docs/business-rules/test-vectors</c> directory, found by walking upwards.</summary>
    /// <remarks>
    /// Located by walking up from the test assembly rather than by a relative path, so the tests
    /// keep working from an IDE, from <c>dotnet test</c>, and from any working directory.
    /// </remarks>
    public static string Directory { get; } = Locate();

    public static IEnumerable<string> Files() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.vectors.json").OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    /// Loads every fixture entry in a file.
    /// </summary>
    /// <remarks>
    /// A file holds either one fixture object or an array of them. The array form exists because
    /// a single implementation file often tests several distinct units — the implementation covers
    /// SigV4, Digest and cookie parsing — and forcing one unit per file would either scatter one
    /// module across many files or force unrelated cases under one <c>unit</c> label.
    /// </remarks>
    public static IReadOnlyList<Fixture> Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var name = Path.GetFileName(path);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.Deserialize<Fixture[]>(Options)
                                   ?? throw new InvalidDataException($"{name} deserialised to null"),
            JsonValueKind.Object => [document.RootElement.Deserialize<Fixture>(Options)
                                     ?? throw new InvalidDataException($"{name} deserialised to null")],
            _ => throw new InvalidDataException(
                $"{name} must be a fixture object or an array of them, was {document.RootElement.ValueKind}"),
        };
    }

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "business-rules", "test-vectors");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find docs/business-rules/test-vectors by walking up from " + AppContext.BaseDirectory);
    }
}

/// <summary>One fixture file: the extracted cases for one implementation file.</summary>
public sealed record Fixture
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public string? SourceFile { get; init; }

    public string? SourceLines { get; init; }

    /// <summary>The case-group names these cases came from.</summary>
    public string[] ExtractedFrom { get; init; } = [];

    /// <summary><c>vector</c> for pure functions, <c>scenario</c> when a seeded environment is needed.</summary>
    public string? Kind { get; init; }

    public string? Unit { get; init; }

    /// <summary>Only present on <c>scenario</c> fixtures; names the seed artefact.</summary>
    public FixtureSetup? Setup { get; init; }

    public FixtureCase[] Cases { get; init; } = [];
}

public sealed record FixtureSetup
{
    public string? SeedSql { get; init; }
}

public sealed record FixtureCase
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    /// <summary>
    /// A per-case seed artefact.
    /// </summary>
    /// <remarks>
    /// The schema puts <c>setup</c> on the fixture, but several The extraction pass fixtures put it on the
    /// case instead, because one scenario file can carry cases that need different seeds. Both
    /// placements are read so a seed reference cannot go unchecked — which is what happened
    /// before this field existed: the seed-exists assertion silently skipped every fixture that
    /// used the per-case form.
    /// </remarks>
    public FixtureSetup? Setup { get; init; }

    public JsonElement Input { get; init; }

    public JsonElement Expected { get; init; }

    public string? Notes { get; init; }
}
