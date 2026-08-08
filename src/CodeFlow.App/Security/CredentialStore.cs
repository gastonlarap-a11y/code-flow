namespace CodeFlow.Security;

/// <summary>
/// Raised when the platform credential store cannot be reached.
/// </summary>
/// <remarks>
/// Its own type because callers must be able to tell "there is no credential saved" from "the
/// store is broken". Collapsing the two is the failure this design exists to prevent: a keyring
/// with no working backend lets writes silently succeed while reads come back empty, and a user
/// then loses credentials without ever seeing an error. Never add a plaintext fallback here.
/// </remarks>
public sealed class CredentialStoreException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The OS credential store: macOS Keychain, Windows Credential Manager.
/// </summary>
/// <remarks>
/// <para>
/// <b>The service name and key formats are a byte-level contract.</b> An existing 1.7.2 install
/// has credentials filed under these exact strings; changing one character does not migrate
/// anything, it silently makes every stored credential unreadable with no error and no way back.
/// See <c>docs/business-rules/10-security.md</c>.
/// </para>
/// <para>
/// There is no plaintext fallback and never will be. A platform with no implementation throws at
/// the first call rather than behaving like an empty store.
/// </para>
/// </remarks>
public static class CredentialStore
{
    /// <summary>The keychain service every credential is filed under. `VERBATIM`.</summary>
    public const string Service = "com.codeflow.app";

    /// <summary>Azure DevOps PAT, keyed per organisation.</summary>
    /// <remarks>
    /// Organisation-scoped because global PATs are being retired; the organisation is a required
    /// part of a credential's identity, not an optional qualifier.
    /// </remarks>
    public static string AdoPatKey(string organisation) => $"ado-pat:{organisation}";

    /// <summary>GitHub token, keyed per host so github.com and Enterprise servers coexist.</summary>
    public static string GitHubTokenKey(string host) => $"github-token:{host}";

    /// <summary>AI provider API key. Deliberately never returned to the frontend.</summary>
    public static string AiApiKey(string provider) => $"ai-api-key:{provider}";

    /// <summary>Reads a secret, or <see langword="null"/> when nothing is stored under that key.</summary>
    /// <exception cref="CredentialStoreException">The store itself failed.</exception>
    public static string? Get(string key) => Backend.Get(Service, key);

    /// <summary>Stores or replaces a secret.</summary>
    /// <exception cref="CredentialStoreException">The store itself failed.</exception>
    public static void Set(string key, string secret) => Backend.Set(Service, key, secret);

    /// <summary>
    /// Removes a secret. Succeeds when there was nothing to remove.
    /// </summary>
    /// <remarks>
    /// "Delete something that is not there" reaches the caller's intended end state either way,
    /// so it is not an error.
    /// </remarks>
    /// <exception cref="CredentialStoreException">The store itself failed.</exception>
    public static void Delete(string key) => Backend.Delete(Service, key);

    /// <summary>Whether a non-blank secret exists. The only read the AI-key family exposes.</summary>
    public static bool Has(string key) => !string.IsNullOrWhiteSpace(Get(key));

    private static ICredentialBackend Backend { get; } = SelectBackend();

    private static ICredentialBackend SelectBackend()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychain();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialManager();
        }

        // Loudly, and at the first call rather than at startup, so a developer building on an
        // unsupported platform can still run everything that does not touch credentials.
        return new UnsupportedPlatform();
    }
}

internal interface ICredentialBackend
{
    string? Get(string service, string account);

    void Set(string service, string account, string secret);

    void Delete(string service, string account);
}

/// <summary>
/// The deliberate dead end for platforms with no implementation.
/// </summary>
/// <remarks>
/// CodeFlow 1.7.2 compiles a keyring backend only for Windows and macOS, which are also the only
/// two entries in its release matrix. Anywhere else its writes silently no-op. That is recorded
/// as <c>DIVERGENCE-SEC-c</c>, and for this port §3 turns it into a requirement: fail, never
/// pretend to be an empty store.
/// </remarks>
internal sealed class UnsupportedPlatform : ICredentialBackend
{
    private static CredentialStoreException Fail() =>
        new($"no credential store is available on {Environment.OSVersion.Platform}. " +
            "CodeFlow targets Windows and macOS; it will not fall back to storing secrets in plaintext.");

    public string? Get(string service, string account) => throw Fail();

    public void Set(string service, string account, string secret) => throw Fail();

    public void Delete(string service, string account) => throw Fail();
}
