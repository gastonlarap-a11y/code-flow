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
/// <b>Under parallel load that contention is observable.</b>
/// <c>CredentialStoreTests.A_stored_secret_is_visible_to_a_separate_process</c> has failed with
/// exit code 24 from <c>/usr/bin/security</c> roughly once in seven full-suite runs, and passes
/// every time in isolation. Each test uses a per-instance GUID key, so it is not a collision over
/// one entry; it is the keychain itself refusing concurrent access.
/// </para>
/// <para>
/// <b>This reduces the contention rather than removing it.</b> xUnit only serialises within a
/// collection, so these six classes no longer overlap each other — but other collections still run
/// alongside them, and nothing here can stop another application on the machine from talking to the
/// same keychain. If the failure reappears, that is why, and it is a test-environment problem
/// rather than a defect in <see cref="CodeFlow.Security.CredentialStore"/>.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialKeychain
{
    public const string Name = "serial-keychain";
}
