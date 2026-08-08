using System.Diagnostics;
using CodeFlow.Ai;
using CodeFlow.Security;

namespace CodeFlow.Update;

/// <summary>
/// Where the token that reads this repository's releases comes from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here ships a credential, and nothing here writes one to disk.</b> The repository is
/// private, so its releases need authentication — and the one thing that must not happen is a token
/// baked into the app or committed alongside it, which is what an embedded updater key would be.
/// </para>
/// <para>
/// So the token is borrowed from something the user already set up, in this order:
/// </para>
/// <list type="number">
/// <item>
/// The GitHub token the app already keeps in the OS keychain for pull requests
/// (<see cref="CredentialStore.GitHubTokenKey"/>). If reviews work, updates work, and the user
/// configures nothing new.
/// </item>
/// <item>
/// <c>gh auth token</c>. A developer with the GitHub CLI logged in has a credential the app can
/// borrow without asking, and <c>gh</c> owns its storage and refresh.
/// </item>
/// </list>
/// <para>
/// Neither being present is a legitimate state, reported as <c>no-credential</c> rather than as a
/// failure — the app simply cannot see its own releases yet.
/// </para>
/// </remarks>
internal static class UpdateCredential
{
    /// <summary>How long <c>gh</c> gets before it is given up on.</summary>
    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The token to read releases with, or null when there is none to borrow.</summary>
    public static async Task<string?> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var stored = CredentialStore.Get(CredentialStore.GitHubTokenKey(host));
            if (!string.IsNullOrWhiteSpace(stored))
            {
                return stored;
            }
        }
        catch (CredentialStoreException)
        {
            // A broken keychain is worth surfacing where a credential is being *saved*; here it
            // only means this source has nothing to offer, and `gh` may still.
        }

        return await FromGitHubCliAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks the GitHub CLI for its token.</summary>
    /// <remarks>
    /// Resolved to a full path first: .NET looks a bare name up in this process's own <c>PATH</c>,
    /// which a Finder-launched app inherits nearly empty. See <c>XLANG-AI-a</c> in
    /// <see cref="BinaryDiscovery.ResolveBinary"/>.
    /// </remarks>
    private static async Task<string?> FromGitHubCliAsync(CancellationToken cancellationToken)
    {
        var executable = BinaryDiscovery.FindOnPath("gh");
        if (executable is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            ArgumentList = { "auth", "token" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GhTimeout);

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var token = output.Trim();

            // `gh` exits non-zero when nobody is logged in, and prints its advice on stderr.
            return process.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or OperationCanceledException
            or IOException)
        {
            return null;
        }
    }
}
