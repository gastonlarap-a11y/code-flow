using System.Text.Json;
using CodeFlow.Tests.TestVectors;

namespace CodeFlow.Tests.Git;

/// <summary>
/// Reads the extracted git scenarios so their expected values live in one place only.
/// </summary>
/// <remarks>
/// The git fixtures are <c>kind: "scenario"</c>: prose <c>steps</c> plus an <c>expected</c> object
/// with keys chosen per case, so unlike the <c>vector</c> fixtures they cannot be replayed
/// mechanically. The steps are followed by hand in each test — but the expected values are read
/// back from the JSON rather than retyped, so the fixture stays the source of truth and the two
/// cannot drift apart unnoticed.
/// </remarks>
internal static class GitFixtures
{
    public static JsonElement Expected(string file, string caseId)
    {
        var path = System.IO.Path.Combine(FixtureCatalog.Directory, file);

        foreach (var fixture in FixtureCatalog.Load(path))
        {
            foreach (var testCase in fixture.Cases)
            {
                if (testCase.Id == caseId)
                {
                    return testCase.Expected;
                }
            }
        }

        throw new InvalidOperationException($"no case '{caseId}' in {file}");
    }

    public static string String(string file, string caseId, string key) =>
        Expected(file, caseId).GetProperty(key).GetString()!;

    public static bool Bool(string file, string caseId, string key) =>
        Expected(file, caseId).GetProperty(key).GetBoolean();

    public static int Int(string file, string caseId, string key) =>
        Expected(file, caseId).GetProperty(key).GetInt32();
}
