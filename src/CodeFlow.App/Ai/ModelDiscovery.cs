using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using CodeFlow.Ai.Engines;

namespace CodeFlow.Ai;

/// <summary>Whether a provider is ready to use, for the Settings status badge.</summary>
/// <param name="Available">Whether launching or reaching it would work right now.</param>
/// <param name="Detail">
/// The resolved path or endpoint when available; the missing binary name, or a short raw reason,
/// when not. The frontend pairs this with a translated label rather than showing it bare.
/// </param>
/// <param name="Binary">The binary or endpoint that was checked.</param>
public sealed record ProviderStatus(bool Available, string Detail, string Binary);

/// <summary>
/// Listing a provider's models, and answering whether it is usable at all.
/// </summary>
/// <remarks>
/// Three strategies, in a fixed order of preference — a catalogue already on disk, a CLI
/// subcommand, an HTTP endpoint — so the picker shows what is actually installed or configured
/// rather than a hardcoded guess that drifts.
/// </remarks>
internal static class ModelDiscovery
{
    /// <summary>Engine versions already probed, keyed by binary. A cached null means "asked, unknown".</summary>
    private static readonly ConcurrentDictionary<string, string?> Versions = new(StringComparer.Ordinal);

    /// <summary>Everything a provider can currently run.</summary>
    /// <remarks>
    /// An empty list is a valid answer, not a failure: it means the engine has no way to enumerate
    /// its models, and the frontend then falls back to its curated list. Only a listing that was
    /// attempted and failed throws.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> ListAsync(
        IAiEngine engine, string binary, HttpClient http, CancellationToken cancellationToken)
    {
        switch (engine.Transport)
        {
            case Transport.Ollama:
                // Degrades to empty rather than throwing: the picker should not hard-fail because
                // the local server is down. The status badge is what reports that.
                try
                {
                    return await Engines.Ollama.FetchTagsAsync(http, binary, cancellationToken).ConfigureAwait(false);
                }
                catch (AiRunFailedException)
                {
                    return [];
                }

            case Transport.OpenAiCompatible { ApiKey: var apiKey }:
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return [];
                }

                try
                {
                    return await Engines.OpenAi.FetchModelsAsync(http, binary, apiKey, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (AiRunFailedException)
                {
                    return [];
                }
        }

        // A catalogue the CLI already wrote to disk beats spawning it, and is the only option for
        // a CLI with no listing subcommand.
        if (engine.CachedModels() is { } cached)
        {
            return cached;
        }

        if (engine.ListModelsArgs is not { } args)
        {
            return [];
        }

        var (success, stdout, stderr) = await CaptureAsync(binary, args, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            var detail = stderr.Trim();
            throw new AiRunFailedException(
                $"'{binary} {string.Join(" ", args)}' failed: {(detail.Length == 0 ? "no output" : detail)}");
        }

        return [.. stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
    }

    /// <summary>
    /// The version of the engine's CLI, so "what answered this?" stays answerable in a conversation
    /// reopened weeks later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cached per binary for the life of the process: otherwise every chat turn would pay for an
    /// extra process spawn, and a CLI does not change version underneath a running app. A failed
    /// probe is cached too, so a missing or older binary is not re-spawned on every message.
    /// </para>
    /// <para>
    /// HTTP engines have no CLI to ask and report <see langword="null"/>; the stamp then omits the
    /// version rather than showing a blank.
    /// </para>
    /// </remarks>
    public static async Task<string?> EngineVersionAsync(
        IAiEngine engine, string binary, CancellationToken cancellationToken)
    {
        if (engine.Transport is not Transport.Subprocess)
        {
            return null;
        }

        if (Versions.TryGetValue(binary, out var cached))
        {
            return cached;
        }

        string? version = null;
        try
        {
            var (success, stdout, stderr) = await CaptureAsync(binary, ["--version"], cancellationToken)
                .ConfigureAwait(false);

            // Not every CLI prints its banner on stdout, so stderr is the second place to look.
            if (success)
            {
                version = AiText.ParseVersion(stdout) ?? AiText.ParseVersion(stderr);
            }
        }
        catch (Exception failure) when (failure is AiRunFailedException or Win32Exception or InvalidOperationException)
        {
            // The binary is not there, or would not launch. Unknown, not fatal: the version is a
            // stamp under a reply, never a precondition for producing one.
        }

        Versions[binary] = version;
        return version;
    }

    /// <summary>Whether a provider is usable right now.</summary>
    /// <remarks>
    /// Subprocess engines are checked by locating their binary; the HTTP ones by asking their
    /// endpoint for its models, which also validates the credential.
    /// </remarks>
    public static async Task<ProviderStatus> ProbeAsync(
        IAiEngine engine, string binary, HttpClient http, CancellationToken cancellationToken)
    {
        switch (engine.Transport)
        {
            case Transport.Ollama:
                try
                {
                    var models = await Engines.Ollama.FetchTagsAsync(http, binary, cancellationToken)
                        .ConfigureAwait(false);
                    return new ProviderStatus(true, $"{binary} · {models.Count} modelos", binary);
                }
                catch (AiRunFailedException ex)
                {
                    return new ProviderStatus(false, ex.Message, binary);
                }

            case Transport.OpenAiCompatible { ApiKey: var apiKey }:
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    // A distinct, machine-readable reason: the frontend routes it to "add your key"
                    // rather than to "the endpoint is unreachable".
                    return new ProviderStatus(false, "missing-api-key", binary);
                }

                try
                {
                    var models = await Engines.OpenAi.FetchModelsAsync(http, binary, apiKey, cancellationToken)
                        .ConfigureAwait(false);
                    return new ProviderStatus(true, $"{binary} · {models.Count} modelos", binary);
                }
                catch (AiRunFailedException ex)
                {
                    return new ProviderStatus(false, ex.Message, binary);
                }

            default:
                var path = BinaryDiscovery.FindOnPath(binary);
                return new ProviderStatus(path is not null, path ?? binary, binary);
        }
    }

    /// <summary>
    /// Runs an auxiliary CLI call — a listing or a version — and captures what it printed.
    /// </summary>
    /// <remarks>
    /// Deliberately not routed through <see cref="AiRunRegistry"/>: these are internal calls with
    /// no run id, and putting them there would stream a model listing into the user's activity log
    /// and give it a stop button.
    /// </remarks>
    private static async Task<(bool Success, string Stdout, string Stderr)> CaptureAsync(
        string binary, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var dirs = BinaryDiscovery.SearchDirs();
        var info = new ProcessStartInfo
        {
            FileName = BinaryDiscovery.ResolveBinary(binary, dirs),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        BinaryDiscovery.ApplyPath(info, dirs);

        using var process = Process.Start(info)
            ?? throw new AiRunFailedException($"could not start {info.FileName}");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (process.ExitCode == 0,
            AiText.StripAnsi(await stdout.ConfigureAwait(false)),
            AiText.StripAnsi(await stderr.ConfigureAwait(false)));
    }
}
