using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Ai;
using CodeFlow.Ipc;
using CodeFlow.Platform;

namespace CodeFlow.Workspaces;

/// <summary>
/// Installs a skill from skills.sh by shelling out to <c>npx skills add</c> (<c>WS-005</c>).
/// </summary>
/// <remarks>
/// The one part of the skills subsystem that reaches the network, and the one this port cannot
/// exercise offline: the tests cover how the command line is built and how the output is published,
/// not a real install.
/// </remarks>
public sealed class SkillInstaller(PublishEvent publish)
{
    /// <summary>
    /// Runs the install and answers the skill name that was created.
    /// </summary>
    /// <remarks>
    /// Streams every line of both stdout and stderr to <c>skills:progress</c> as it arrives. The
    /// two are deliberately indistinguishable once published — 1.7.2 emits them through one
    /// event with one field, so the settings screen shows an install log rather than two channels.
    /// </remarks>
    public async Task<string> InstallAsync(
        string workspaceId,
        string sourceRepo,
        string skillName,
        CancellationToken cancellationToken)
    {
        var workingDirectory = AppPaths.WorkspaceSkillsDirectory(workspaceId);
        Directory.CreateDirectory(workingDirectory);

        var startInfo = NpxCommand();
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        foreach (var arg in new[] { "--yes", "skills", "add", sourceRepo, "--skill", skillName })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = StartOrThrow(startInfo);

        // CodeFlow 1.7.2 gives npx a null stdin so a prompt cannot hang the install waiting for an
        // answer nobody can give.
        process.StandardInput.Close();

        var stdout = PumpAsync(process.StandardOutput, cancellationToken);
        var stderr = PumpAsync(process.StandardError, cancellationToken);

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        var stdoutLines = await stdout.ConfigureAwait(false);
        var stderrLines = await stderr.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var detail = stderrLines.Count > 0 ? string.Join("\n", stderrLines) : string.Join("\n", stdoutLines);

            throw new InvalidOperationException($"npx skills add failed: {detail}");
        }

        // A zero exit is not taken on trust. `npx skills add` can report success for a skill name
        // or repo that produced nothing, and recording a row for a folder that does not exist would
        // leave the workspace listing a skill nobody can open.
        var installed = SkillFiles.Directory(SkillFiles.RootFor(workspaceId), skillName);
        if (!Directory.Exists(installed))
        {
            throw new InvalidOperationException(
                $"skills add reported success but {installed} wasn't created — check the skill name and repo");
        }

        return skillName;
    }

    /// <summary>
    /// The command that runs <c>npx</c>.
    /// </summary>
    /// <remarks>
    /// <b>The Windows shim is deliberate and marked <c>DIVERGENCE</c> in <c>WS-005</c>.</b> On
    /// Windows <c>npx</c> is a <c>.cmd</c> shim, and starting it directly does not launch at all —
    /// the same class of problem as <c>code</c> in <c>FileOps.OpenInVsCode</c>. Simplifying this
    /// back to a plain <c>npx</c> would break skill installation on Windows only, which is the
    /// platform nothing here can test.
    /// </remarks>
    internal static ProcessStartInfo NpxCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Resolved rather than passed bare, for the reason in BinaryDiscovery.ResolveBinary's
            // XLANG-AI-a note: .NET looks a bare name up in *this* process's PATH, which a
            // Finder-launched macOS app inherits from launchd with nothing on it. Not sufficient on
            // its own — npx is a script whose `#!/usr/bin/env node` still needs node on PATH, which
            // is the shell's `applyLoginShellPath` — but it removes one way to fail.
            return new ProcessStartInfo(BinaryDiscovery.FindOnPath("npx") ?? "npx");
        }

        var startInfo = new ProcessStartInfo("cmd");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("npx");

        return startInfo;
    }

    private static Process StartOrThrow(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new InvalidOperationException("failed to launch npx");
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"failed to launch npx: {e.Message}");
        }
    }

    /// <summary>Publishes each line as it is read, and keeps them for the failure message.</summary>
    private async Task<List<string>> PumpAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var collected = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            collected.Add(line);

            using var payload = JsonSerializer.SerializeToDocument(
                new SkillProgressEvent(line), SkillEventJsonContext.Default.SkillProgressEvent);

            await publish("skills:progress", payload.RootElement, cancellationToken).ConfigureAwait(false);
        }

        return collected;
    }
}

/// <summary>One line of an install's output.</summary>
internal sealed record SkillProgressEvent(string Line);

/// <summary>
/// The install event's payload.
/// </summary>
/// <remarks>
/// camelCase, and with one field the casing cannot distinguish — <c>line</c> is <c>line</c> either
/// way. The policy is stated rather than left to chance because the next field added would not be
/// so forgiving.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SkillProgressEvent))]
internal sealed partial class SkillEventJsonContext : JsonSerializerContext;
