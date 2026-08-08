using CodeFlow.Security;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// A GitHub token in the real OS credential store, removed when the test ends.
/// </summary>
/// <remarks>
/// <para>
/// The GitHub counterpart of <see cref="TempAdoPat"/>, and it exists for the same reason: the commands
/// load their credential through <see cref="CredentialStore"/> with no seam, so a test that wants to
/// reach past "no token saved" has to put one there.
/// </para>
/// <para>
/// <b>Never <c>github.com</c>.</b> That key is where a developer's own token lives, and this type both
/// writes and deletes — pointing it at the real host would overwrite a working credential and then
/// remove it. So the host is unique per instance and ends in <c>.invalid</c>, which RFC 2606 reserves
/// precisely so it can never resolve. The project under test is linked to that host, which is enough:
/// the client builds an Enterprise API root from it and the fake transport answers regardless.
/// </para>
/// </remarks>
internal sealed class TempGitHubToken : IDisposable
{
    private readonly string _key;

    public TempGitHubToken(string host, string token = "test-token")
    {
        Xunit.Assert.SkipUnless(
            OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(),
            "CodeFlow targets Windows and macOS; there is deliberately no fallback credential store elsewhere.");

        _key = CredentialStore.GitHubTokenKey(host);
        CredentialStore.Set(_key, token);
    }

    /// <summary>A host no real token can be filed under, unique to one test.</summary>
    public static string UniqueHost() => $"github-{Guid.NewGuid():N}.invalid";

    public void Dispose()
    {
        try
        {
            CredentialStore.Delete(_key);
        }
        catch (CredentialStoreException)
        {
            // Nothing to clean up on a platform with no backend.
        }
    }
}
