using Xunit;

namespace CodeFlow.Tests;

/// <summary>
/// Tests that write to the real OS credential store, and so must not run beside each other.
/// </summary>
/// <remarks>
/// <para>
/// These tests deliberately use the actual keychain rather than a double — <c>.claude/rules/dotnet.md</c>
/// demands a test that proves persistence, and a fake would pass while the real backend silently
/// no-ops. The cost is that they contend for a resource outside the process: on macOS the login
/// keychain, reached through both the Security framework and the <c>security</c> CLI.
/// </para>
/// <para>
/// <b>Serialising these is worth doing on its own merits</b> — several classes writing to one
/// keychain under parallel load is a race worth not having, and the keys are per-instance GUIDs so
/// nothing here depends on ordering.
/// </para>
/// <para>
/// <b>What this is not for.</b> An earlier version of this comment claimed it was mitigating the
/// intermittent failure of
/// <c>CredentialStoreTests.A_stored_secret_is_visible_to_a_separate_process</c>, which exited 24
/// from <c>/usr/bin/security</c>. That diagnosis was wrong. macOS binds a keychain item's ACL to
/// the binary that created it, and the test was asking <c>security</c> — a different binary — for
/// the <em>secret</em> rather than the item, which needs an authorisation prompt nobody was there
/// to answer. Concurrency was never involved: it failed the same way run entirely alone. That test
/// now reads attributes instead, and is deterministic. If it fails again it means the credential
/// really was not stored.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialKeychain
{
    public const string Name = "serial-keychain";
}
