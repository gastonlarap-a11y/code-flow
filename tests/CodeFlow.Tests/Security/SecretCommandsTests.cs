using CodeFlow.Ipc;
using CodeFlow.Security;
using Xunit;

namespace CodeFlow.Tests.Security;

/// <summary>
/// The shape of the credential command surface.
/// See <c>docs/business-rules/10-security.md</c>, <c>DIVERGENCE-SEC-d</c>.
/// </summary>
/// <remarks>
/// <para>
/// This asserts an absence, which is unusual and deliberate. 1.7.2 exposed <c>get_ado_pat</c> and
/// <c>get_github_token</c>, which returned the plaintext secret to the renderer, and the port
/// reproduced them faithfully. They are gone; the renderer asks <c>has_*</c> instead.
/// </para>
/// <para>
/// The absence is the security property, so it needs a test the way a behaviour does. Re-adding
/// either command — by a merge, by copying the <c>set</c> pair, or by "restoring" 1.7.2 parity —
/// fails here rather than silently widening the surface again.
/// </para>
/// </remarks>
public sealed class SecretCommandsTests
{
    private static CommandRegistry Registry() => new CommandRegistry().AddSecretCommands().Seal();

    [Theory]
    [InlineData("get_ado_pat")]
    [InlineData("get_github_token")]
    [InlineData("get_ai_api_key")]
    public void No_command_hands_a_credential_back_to_the_renderer(string command)
    {
        Assert.False(Registry().TryGet(command, out _));
    }

    [Theory]
    [InlineData("set_ado_pat")]
    [InlineData("has_ado_pat")]
    [InlineData("delete_ado_pat")]
    [InlineData("set_github_token")]
    [InlineData("has_github_token")]
    [InlineData("delete_github_token")]
    [InlineData("set_ai_api_key")]
    [InlineData("has_ai_api_key")]
    [InlineData("delete_ai_api_key")]
    public void Every_credential_family_offers_set_has_and_delete(string command)
    {
        Assert.True(Registry().TryGet(command, out _));
    }

    [Fact]
    public void The_credential_surface_is_exactly_nine_commands()
    {
        // Pinned so a tenth command is a decision someone takes on purpose. The count is the one
        // in the class doc and in docs/business-rules/01-ipc-surface.md; all three move together.
        Assert.Equal(9, Registry().Count);
    }
}
