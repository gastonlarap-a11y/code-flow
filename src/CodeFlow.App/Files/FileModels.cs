using System.Text.Json.Serialization;

namespace CodeFlow.Files;

/// <summary>One row of the explorer's tree.</summary>
/// <param name="Path">Repo-relative, forward-slash normalised.</param>
public sealed record FileEntry(string Name, string Path, bool IsDir);

/// <summary>One matching line.</summary>
/// <param name="Path">Repo-relative, <c>/</c>-separated.</param>
/// <param name="LineNo">1-based, so it can be handed straight to the editor.</param>
public sealed record SearchHit(string Path, uint LineNo, string Line);

/// <summary>What a search answers.</summary>
/// <param name="Truncated">
/// True when the result set hit <c>maxResults</c> — the UI says so instead of implying the list is
/// everything there is. The other three caps in <c>FILE-008</c> are silent by design.
/// </param>
public sealed record SearchOutcome(IReadOnlyList<SearchHit> Hits, bool Truncated);

/// <summary>What a repo-wide replace answers.</summary>
/// <param name="CheckpointId">
/// The snapshot taken before anything was written, so a repo-wide replace is undoable from the same
/// place an AI run is. <c>null</c> only if the snapshot itself failed.
/// </param>
public sealed record ReplaceOutcome(int Replacements, int Files, string? CheckpointId);

/// <summary>
/// The toggles an editor's find box carries, in one place so search and replace cannot drift apart
/// on what "a match" means.
/// </summary>
/// <remarks>
/// The one shape in this feature that is camelCase in <em>both</em> directions, because it only
/// ever travels inbound.
/// Everything this feature returns is snake_case — see <see cref="FileJsonContext"/>.
/// </remarks>
public sealed record SearchOptions
{
    [JsonPropertyName("caseSensitive")]
    public bool CaseSensitive { get; init; }

    /// <summary>Match only whole words — <c>set</c> stops matching inside <c>offset</c>.</summary>
    [JsonPropertyName("wholeWord")]
    public bool WholeWord { get; init; }

    /// <summary>Treat the query as a regular expression rather than literal text.</summary>
    [JsonPropertyName("regex")]
    public bool Regex { get; init; }

    /// <summary>
    /// Comma-separated globs limiting which files are searched (<c>src/**, *.ts</c>). Empty = all.
    /// </summary>
    [JsonPropertyName("include")]
    public string Include { get; init; } = string.Empty;

    /// <summary>Comma-separated globs to skip, applied after <see cref="Include"/>.</summary>
    [JsonPropertyName("exclude")]
    public string Exclude { get; init; } = string.Empty;
}
