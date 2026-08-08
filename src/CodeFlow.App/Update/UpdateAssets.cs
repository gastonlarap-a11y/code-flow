namespace CodeFlow.Update;

/// <summary>
/// Which artefact on a release belongs to the machine asking, and what can be done with it.
/// </summary>
/// <remarks>
/// The names come from <c>shell/electron-builder.yml</c>: a macOS build produces
/// <c>CodeFlow-&lt;version&gt;-arm64.dmg</c> and a matching <c>.zip</c>, a Windows build an NSIS
/// <c>.exe</c>. Matching by extension rather than by full name keeps this working when the version
/// or the architecture in the filename changes.
/// </remarks>
internal static class UpdateAssets
{
    /// <summary>The app installs the update itself and restarts into it.</summary>
    public const string Auto = "auto";

    /// <summary>The app can only put the artefact in front of the user.</summary>
    public const string Manual = "manual";

    /// <summary>The marker that tells the Windows installer from the portable build.</summary>
    /// <remarks>
    /// Set by <c>win.artifactName</c> in <c>shell/electron-builder.yml</c>; the two names have to
    /// move together. A Windows release carries both an NSIS installer and a portable executable,
    /// and they differ only in their name.
    /// </remarks>
    private const string InstallerMarker = "-Setup-";

    /// <summary>Picks this platform's artefact, or null when the release carries none.</summary>
    /// <remarks>
    /// On Windows "this platform's artefact" is the **installer**, not merely the first
    /// <c>.exe</c>. A release carries a portable build too, and taking whichever the API happened
    /// to list first meant `InstallKind() == "auto"` could launch a portable copy of the new
    /// version instead of installing it — leaving the old build installed and the user looking at
    /// something that updates nothing.
    /// </remarks>
    public static ReleaseAsset? For(IReadOnlyList<ReleaseAsset> assets)
    {
        var extension = Extension();

        // A blockmap sits next to the installer with almost the same name and is not an installer.
        // Neither is a `.sha256`, which ends in its own extension and so never matches here.
        var candidates = assets
            .Where(a => a.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                && !a.Name.EndsWith(".blockmap", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!OperatingSystem.IsWindows())
        {
            return candidates.FirstOrDefault();
        }

        // Fall back to the first candidate rather than to nothing: a release built before the
        // artefact names were made explicit carries one unmarked `.exe`, and refusing to see it
        // would be a worse answer than offering it.
        return candidates.FirstOrDefault(a => a.Name.Contains(InstallerMarker, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Whether this platform can apply an update on its own.
    /// </summary>
    /// <remarks>
    /// <b>macOS cannot, and saying otherwise would be the same lie this replaces.</b> Applying an
    /// update in place means replacing the running <c>.app</c>, and macOS refuses to launch a
    /// bundle whose signature does not match what Gatekeeper recorded — an unsigned app that
    /// overwrites itself is a bundle the user then has to re-approve, or that will not open at all.
    /// The app is unsigned (<c>identity: null</c> in <c>electron-builder.yml</c>), so the honest
    /// thing is to hand over the <c>.dmg</c> and say so.
    /// <para>
    /// Windows is different: the NSIS installer is a separate process that replaces the app after
    /// it exits, and nothing there validates a signature before running it. SmartScreen warns, once.
    /// </para>
    /// </remarks>
    public static string InstallKind() => OperatingSystem.IsWindows() ? Auto : Manual;

    private static string Extension() => OperatingSystem.IsWindows() ? ".exe" : ".dmg";
}
