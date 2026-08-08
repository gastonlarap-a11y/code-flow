using System.Net.Security;

namespace CodeFlow.ApiClient;

/// <summary>
/// What a streaming connection accepts from a server's TLS certificate.
/// </summary>
/// <remarks>
/// <para>
/// One rule, in one place, because <c>BUG-API-d</c> is what happens when there are three. The
/// reference has a separate verifier per protocol and they disagree: the WebSocket's keeps real
/// signature checking, while MQTT's and gRPC's skip verification entirely — so turning off
/// <c>verify_ssl</c> meant something different depending on which protocol the user picked, and the
/// weakest of the three was not the one anybody would have guessed. The bug report names the
/// WebSocket's as the model. This is it, shared rather than copied, so the three cannot drift again.
/// </para>
/// <para>
/// <b>HTTP is deliberately not on this rule.</b> <c>HttpSend.BuildHandler</c> accepts any certificate
/// when <c>verify_ssl</c> is off, which is curl's <c>-k</c> and the affordance an API tester exists
/// to provide. <c>BUG-API-d</c> is about the three streaming verifiers disagreeing with each other,
/// not about that toggle.
/// </para>
/// </remarks>
internal static class StreamTlsPolicy
{
    /// <summary>
    /// Whether a certificate that produced <paramref name="errors"/> is acceptable.
    /// </summary>
    /// <remarks>
    /// With verification on, only a clean result passes — the platform has already done the work and
    /// its answer stands. With it off, the two errors a self-signed staging server actually produces
    /// are forgiven: an issuer nothing trusts, and a name that does not match. Everything else still
    /// refuses, and a signature that does not verify is the case that matters — a certificate whose
    /// signature is wrong is not a misconfiguration, it is someone else's certificate.
    /// </remarks>
    public static bool Accepts(SslPolicyErrors errors, bool verifySsl) =>
        verifySsl
            ? errors == SslPolicyErrors.None
            : (errors & ~(SslPolicyErrors.RemoteCertificateChainErrors
                        | SslPolicyErrors.RemoteCertificateNameMismatch)) == 0;
}
