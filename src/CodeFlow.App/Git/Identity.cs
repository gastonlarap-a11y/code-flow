using LibGit2Sharp;

namespace CodeFlow.Git;

/// <summary>The globally configured commit identity. Either half may be unset.</summary>
public sealed record GitIdentity(string? Name, string? Email);

/// <summary>
/// The global <c>user.name</c> / <c>user.email</c>.
/// </summary>
/// <remarks>
/// Process-global, not per-repository (GIT-027): both commands read and write the global
/// configuration stack, which is why neither takes a <c>repo_path</c>. This is also the identity
/// <see cref="IRepository.Config"/> falls back to for a repository with no local override, so it
/// is what <c>commit</c> signs with when the caller supplies no author.
/// </remarks>
public static class Identity
{
    /// <summary>Reads the configured identity. An unset half is <c>null</c>, not an error.</summary>
    public static GitIdentity Get()
    {
        // No repository configuration file: this is the global stack on purpose (GIT-027), and
        // the public constructor is protected, so BuildFrom is the only way in.
        using var config = Configuration.BuildFrom(null);
        return new GitIdentity(
            config.Get<string>("user.name")?.Value,
            config.Get<string>("user.email")?.Value);
    }

    /// <summary>Writes both halves to the global configuration.</summary>
    public static void Set(string name, string email)
    {
        // No repository configuration file: this is the global stack on purpose (GIT-027), and
        // the public constructor is protected, so BuildFrom is the only way in.
        using var config = Configuration.BuildFrom(null);
        config.Set("user.name", name, ConfigurationLevel.Global);
        config.Set("user.email", email, ConfigurationLevel.Global);
    }
}
