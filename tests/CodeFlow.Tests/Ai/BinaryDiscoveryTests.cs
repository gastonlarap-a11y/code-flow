using CodeFlow.Ai;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// Where an AI CLI is looked for, and in what order.
/// See <c>docs/business-rules/05-ai-engines.md</c> <c>AI-005</c>–<c>AI-007</c>.
/// </summary>
/// <remarks>
/// Worth more than it looks: every one of these rules exists because a real install was invisible
/// to the app, and the symptom is always the same unhelpful "binary not found" on a machine where
/// the CLI plainly works in a terminal.
/// </remarks>
public sealed class BinaryDiscoveryTests
{
    [Fact]
    public void The_install_dirs_come_before_the_inherited_path()
    {
        var install = BinaryDiscovery.InstallDirs();
        var search = BinaryDiscovery.SearchDirs();

        // The whole point: a macOS app launched from Finder inherits a PATH without Homebrew, so
        // an installer directory has to win over whatever PATH happens to hold.
        Assert.Equal(install, search.Take(install.Count));
        Assert.True(search.Count > install.Count, "the process PATH should have been appended");
    }

    [Fact]
    public void The_install_dirs_cover_the_places_the_cli_installers_use()
    {
        var dirs = BinaryDiscovery.InstallDirs();

        Assert.Contains(dirs, d => d.EndsWith(Path.Combine(".claude", "local"), StringComparison.Ordinal));
        Assert.Contains(dirs, d => d.EndsWith(Path.Combine(".opencode", "bin"), StringComparison.Ordinal));
        Assert.Contains(dirs, d => d.EndsWith(Path.Combine(".local", "bin"), StringComparison.Ordinal));

        if (OperatingSystem.IsWindows())
        {
            // Without this one a working Codex desktop install probes as "not found": the app
            // ships its CLI here and deliberately keeps it off PATH.
            Assert.Contains(dirs, d => d.EndsWith(Path.Combine("OpenAI", "Codex", "bin"), StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains("/opt/homebrew/bin", dirs);
            Assert.Contains("/usr/local/bin", dirs);
        }
    }

    [Fact]
    public void A_configured_path_is_used_exactly_as_stored()
    {
        // AI-007: an explicit binary_path bypasses discovery entirely — no directory search, no
        // extension resolution — so a user who points at one build never silently gets another.
        const string Explicit = "/opt/custom/bin/claude";

        Assert.Equal(Explicit, BinaryDiscovery.ResolveBinary(Explicit, BinaryDiscovery.SearchDirs()));
    }

    [Fact]
    public void A_binary_outside_the_process_path_still_resolves_to_a_full_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Windows path is covered by the two extension tests.");

        // XLANG-AI-a, and the reason a working `agy` install failed to launch from a Finder-opened
        // app. The install directories are searched by FindOnPath but are *not* on the app's own
        // PATH, and .NET's Process.Start looks a bare name up in the parent's PATH — never in the
        // ProcessStartInfo.Environment being prepared for the child. So a bare name here means the
        // launch searches somewhere the detection never did, and the two disagree.
        //
        // The temp directory stands in for ~/.local/bin: present in the search dirs, absent from
        // the process PATH. Exactly the shape of the real failure.
        using var dir = new TempDir();
        var installed = Path.Combine(dir.Path, "cf-fake-cli");
        File.WriteAllText(installed, "");

        Assert.Equal(installed, BinaryDiscovery.ResolveBinary("cf-fake-cli", [dir.Path]));
    }

    [Fact]
    public void Launching_and_detecting_look_in_the_same_place()
    {
        // FindOnPath's contract is "locates a binary the same way a run would". That was false off
        // Windows until XLANG-AI-a, and a contract nothing checks is how it became false.
        var found = BinaryDiscovery.FindOnPath("sh");
        Assert.NotNull(found);

        Assert.Equal(found, BinaryDiscovery.ResolveBinary("sh", BinaryDiscovery.SearchDirs()));
    }

    [Fact]
    public void An_unresolvable_name_is_passed_through_untouched()
    {
        // The fallback is what keeps this from ever being worse than leaving the name alone: the
        // OS gets its own chance, and the failure message still names what the user asked for.
        const string Missing = "codeflow-no-such-cli-3f9a2c";

        Assert.Equal(Missing, BinaryDiscovery.ResolveBinary(Missing, BinaryDiscovery.SearchDirs()));
    }

    [Fact]
    public void A_dot_in_the_name_does_not_stop_the_search_off_windows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows an extension means the name is already complete.");

        // A dot is part of the filename on Unix, not an extension to be matched. Treating
        // `some.tool` as "already resolved" would send the bare name to Process.Start and land back
        // in the bug this fixes.
        using var dir = new TempDir();
        var installed = Path.Combine(dir.Path, "some.tool");
        File.WriteAllText(installed, "");

        Assert.Equal(installed, BinaryDiscovery.ResolveBinary("some.tool", [dir.Path]));
    }

    [Fact]
    public void On_windows_an_exe_beats_a_cmd_shim_in_the_same_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Extension resolution only applies on Windows.");

        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "tool.cmd"), "");
        File.WriteAllText(Path.Combine(dir.Path, "tool.exe"), "");

        Assert.Equal(Path.Combine(dir.Path, "tool.exe"), BinaryDiscovery.ResolveBinary("tool", [dir.Path]));
    }

    [Fact]
    public void On_windows_an_earlier_directory_beats_a_later_one_whatever_the_extension()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Extension resolution only applies on Windows.");

        using var first = new TempDir();
        using var second = new TempDir();
        File.WriteAllText(Path.Combine(first.Path, "tool.cmd"), "");
        File.WriteAllText(Path.Combine(second.Path, "tool.exe"), "");

        // Directory order wins over extension preference — the preference only breaks ties inside
        // one directory.
        Assert.Equal(Path.Combine(first.Path, "tool.cmd"),
            BinaryDiscovery.ResolveBinary("tool", [first.Path, second.Path]));
    }

    [Fact]
    public void Finding_a_binary_that_is_not_there_returns_null()
    {
        Assert.Null(BinaryDiscovery.FindOnPath("codeflow-no-such-cli-3f9a2c"));
        Assert.Null(BinaryDiscovery.FindOnPath("/opt/definitely/not/here"));
    }

    [Fact]
    public void Finding_a_binary_that_is_there_returns_its_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX path that is always present.");

        // /bin is on PATH everywhere this runs, so this exercises the search rather than a fixture.
        Assert.NotNull(BinaryDiscovery.FindOnPath("sh"));
        Assert.Equal("/bin/sh", BinaryDiscovery.FindOnPath("/bin/sh"));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cf-bin-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
