using CodeFlow.Ai.Engines;
using CodeFlow.Security;

namespace CodeFlow.Ai;

/// <summary>
/// The one place a stored provider id becomes an engine.
/// </summary>
/// <remarks>
/// See <c>docs/business-rules/05-ai-engines.md</c> <c>AI-001</c>.
/// </remarks>
public static class EngineCatalog
{
    /// <summary>The provider id everything falls back to.</summary>
    public const string FallbackProvider = "claude";

    /// <summary>Every provider id the backend recognises, for tests and for the settings UI.</summary>
    /// <remarks>
    /// <c>local</c> is an alias of <c>ollama</c> and is deliberately listed: it is a stored value
    /// an existing install may already have.
    /// </remarks>
    public static readonly IReadOnlyList<string> KnownProviders =
        ["claude", "codex", "gemini", "opencode", "ollama", "local", "openai"];

    /// <summary>
    /// Resolves a provider id to its engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anything unrecognised — including an empty string — resolves to Claude.</b> That fallback
    /// is what guarantees a corrupt or missing <c>ai_provider</c> setting, or one written by a
    /// version that supported a provider this one dropped, never leaves the app with no working
    /// engine.
    /// </para>
    /// <para>
    /// The OpenAI key is read from the OS keychain right here so it rides along inside the
    /// engine's <see cref="Transport"/>. That is deliberate and load-bearing: no operation
    /// signature grows an api-key parameter, and because the key lives on the transport rather
    /// than in an invocation, there is no path by which it could reach a subprocess's argv or
    /// environment.
    /// </para>
    /// </remarks>
    public static IAiEngine EngineFor(string provider) => provider switch
    {
        "codex" => new Codex(),
        "gemini" => new Gemini(),
        "opencode" => new OpenCode(),
        "ollama" or "local" => new Ollama(),
        "openai" => new OpenAi(ReadApiKey("openai")),
        _ => new Claude(),
    };

    /// <summary>
    /// Reads a provider's stored API key, treating an unavailable store as "no key".
    /// </summary>
    /// <remarks>
    /// The only place in the app that swallows a credential-store failure, and only because the
    /// caller cannot act on it: resolving an engine happens on every routing lookup, including
    /// ones that never touch the network. A missing key surfaces where it matters instead — the
    /// provider probe reports <c>missing-api-key</c> and the run fails with the endpoint's own 401.
    /// </remarks>
    private static string ReadApiKey(string provider)
    {
        try
        {
            return CredentialStore.Get(CredentialStore.AiApiKey(provider)) ?? string.Empty;
        }
        catch (CredentialStoreException)
        {
            return string.Empty;
        }
    }
}
