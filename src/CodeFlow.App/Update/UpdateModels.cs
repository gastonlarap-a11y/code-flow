using System.Text.Json.Serialization;

namespace CodeFlow.Update;

/// <summary>
/// What a release offers this machine, or why it cannot say.
/// </summary>
/// <param name="Available">
/// False both when the app is current and when the check could not run. <paramref name="Reason"/>
/// tells the two apart, because "you are up to date" and "I could not find out" are different
/// answers and the panel must not print the first when it means the second.
/// </param>
/// <param name="Reason">
/// Empty when the check ran. Otherwise a stable id the renderer maps to a sentence:
/// <c>no-credential</c>, <c>unauthorized</c>, <c>no-release</c>, <c>no-asset</c>, <c>unreachable</c>.
/// </param>
/// <param name="InstallKind">
/// <c>auto</c> when the downloaded artefact installs itself and the app restarts into the new
/// build; <c>manual</c> when all the app can do is put the artefact in front of the user. macOS is
/// <c>manual</c> until the app is signed — see <see cref="UpdateAssets"/>.
/// </param>
public sealed record UpdateAvailability(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("version")] string Version = "",
    [property: JsonPropertyName("notes")] string Notes = "",
    [property: JsonPropertyName("date")] string Date = "",
    [property: JsonPropertyName("asset_name")] string AssetName = "",
    [property: JsonPropertyName("asset_url")] string AssetUrl = "",
    [property: JsonPropertyName("asset_size")] long AssetSize = 0,
    [property: JsonPropertyName("install_kind")] string InstallKind = "manual",
    [property: JsonPropertyName("reason")] string Reason = "");

/// <summary>Where a downloaded artefact landed, and what happened when it was handed over.</summary>
public sealed record UpdateInstallation(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("install_kind")] string InstallKind);

/// <summary>Progress of the download, published as <c>update:progress</c>.</summary>
/// <param name="Total"><c>0</c> when the server sent no length — the UI then shows no percentage.</param>
public sealed record UpdateProgress(
    [property: JsonPropertyName("downloaded")] long Downloaded,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("done")] bool Done);

/// <summary>One asset on a GitHub release.</summary>
internal sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("size")] long Size);

/// <summary>The parts of GitHub's release payload this reads.</summary>
internal sealed record ReleasePayload(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("published_at")] string? PublishedAt,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] IReadOnlyList<ReleaseAsset>? Assets);
