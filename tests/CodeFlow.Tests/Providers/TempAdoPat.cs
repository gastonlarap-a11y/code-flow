using CodeFlow.Security;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// An Azure DevOps PAT in the real OS credential store, removed when the test ends.
/// </summary>
/// <remarks>
/// <para>
/// The real store rather than a double, for the reason <c>CredentialStoreTests</c> gives: a fake would
/// pass while the actual backend silently no-ops. Here it also buys something specific — the commands
/// load their credential through <see cref="CredentialStore"/> with no seam, so a test that wants to
/// reach past "no token saved" has to put one there.
/// </para>
/// <para>
/// The organisation is unique per instance, so nothing collides with a real connection the developer has
/// saved and a failed run cannot leave a recognisable-looking credential behind.
/// </para>
/// </remarks>
internal sealed class TempAdoPat : IDisposable
{
    private readonly string _key;

    public TempAdoPat(string org, string pat = "test-pat")
    {
        Xunit.Assert.SkipUnless(
            OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(),
            "CodeFlow targets Windows and macOS; there is deliberately no fallback credential store elsewhere.");

        _key = CredentialStore.AdoPatKey(org);
        CredentialStore.Set(_key, pat);
    }

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
