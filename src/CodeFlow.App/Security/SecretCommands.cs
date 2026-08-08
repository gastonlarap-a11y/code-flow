using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Ipc;

namespace CodeFlow.Security;

/// <summary>
/// The nine credential commands.
/// </summary>
/// <remarks>
/// <para>
/// Every family is now symmetric: <c>set</c>, <c>has</c>, <c>delete</c>. **No command returns a
/// secret.** 1.7.2 gave ADO PATs and GitHub tokens a <c>get</c> that handed the plaintext to the
/// renderer, and the port reproduced it (<c>DIVERGENCE-SEC-a</c>). Both are gone —
/// <c>DIVERGENCE-SEC-d</c> in <c>docs/business-rules/10-security.md</c> records the decision.
/// </para>
/// <para>
/// The reason it could be taken at all is that nothing needed them: <c>get_github_token</c> had no
/// caller in the renderer, and <c>get_ado_pat</c>'s one caller used the value as a boolean —
/// "is a PAT still saved for this org?" — while migrating a pre-multi-org install. A <c>has</c>
/// answers that question without the secret crossing the IPC boundary, which is what makes an XSS
/// in the renderer unable to exfiltrate a credential rather than merely unlikely to.
/// </para>
/// <para>
/// <c>SecretCommandsTests</c> asserts the two <c>get</c>s are absent from the registry, so
/// reintroducing one fails the suite rather than quietly widening the surface again.
/// </para>
/// </remarks>
public static class SecretCommands
{
    public static CommandRegistry AddSecretCommands(this CommandRegistry registry) =>
        registry
            .Add("set_ado_pat", (p, _) => Run(() =>
            {
                CredentialStore.Set(CredentialStore.AdoPatKey(Arg(p, "org")), Arg(p, "pat"));
                return Unit();
            }))
            .Add("has_ado_pat", (p, _) => Run(() =>
                Bool(CredentialStore.Has(CredentialStore.AdoPatKey(Arg(p, "org"))))))
            .Add("delete_ado_pat", (p, _) => Run(() =>
            {
                CredentialStore.Delete(CredentialStore.AdoPatKey(Arg(p, "org")));
                return Unit();
            }))
            .Add("set_github_token", (p, _) => Run(() =>
            {
                CredentialStore.Set(CredentialStore.GitHubTokenKey(Arg(p, "host")), Arg(p, "token"));
                return Unit();
            }))
            .Add("has_github_token", (p, _) => Run(() =>
                Bool(CredentialStore.Has(CredentialStore.GitHubTokenKey(Arg(p, "host"))))))
            .Add("delete_github_token", (p, _) => Run(() =>
            {
                CredentialStore.Delete(CredentialStore.GitHubTokenKey(Arg(p, "host")));
                return Unit();
            }))
            .Add("set_ai_api_key", (p, _) => Run(() =>
            {
                CredentialStore.Set(CredentialStore.AiApiKey(Arg(p, "provider")), Arg(p, "key"));
                return Unit();
            }))
            .Add("has_ai_api_key", (p, _) => Run(() =>
                Bool(CredentialStore.Has(CredentialStore.AiApiKey(Arg(p, "provider"))))))
            .Add("delete_ai_api_key", (p, _) => Run(() =>
            {
                CredentialStore.Delete(CredentialStore.AiApiKey(Arg(p, "provider")));
                return Unit();
            }));

    private static ValueTask<ReadOnlyMemory<byte>> Run(Func<byte[]> work) =>
        ValueTask.FromResult<ReadOnlyMemory<byte>>(work());

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>A command returning <c>Result&lt;(), String&gt;</c> resolves to null on the wire.</summary>
    private static byte[] Unit() => "null"u8.ToArray();

    /// <summary>
    /// The only shape a credential command may answer with. There is deliberately no string
    /// equivalent: a secret must not be serialisable out of this file.
    /// </summary>
    private static byte[] Bool(bool value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, SecretsJsonContext.Default.Boolean);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(bool))]
internal sealed partial class SecretsJsonContext : JsonSerializerContext;
