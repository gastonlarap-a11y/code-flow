using System.Text.Json.Serialization;

namespace CodeFlow.Providers;

/// <summary>Where a finding sits in a file, as the renderer sends it.</summary>
/// <remarks>
/// <b>camelCase on the wire</b>, unlike almost everything else this application exchanges: it is
/// an inbound argument shape rather than a stored one, and the renderer sends it that way.
/// Spelled out per property
/// rather than given its own serializer context, which two records would not earn.
/// </remarks>
public sealed record CommentLocation(
    string File,
    [property: JsonPropertyName("startLine")] long StartLine,
    [property: JsonPropertyName("endLine")] long EndLine);

/// <summary>One finding the user picked to publish, as it arrives from the renderer.</summary>
/// <remarks>
/// <paramref name="File"/> and <paramref name="Category"/> are the identity that matches it back to a
/// stored finding, so its thread is reused rather than duplicated. Its own single-word property names
/// need no renaming.
/// </remarks>
public sealed record PostFindingItem(
    string? File,
    string Category,
    string Content,
    CommentLocation? Location);

/// <summary>
/// One selected finding, ready to be published: what to say, where, and what was said about it before.
/// </summary>
/// <remarks>
/// Assembled by the command from the user's selection joined to the run's stored memory, so a host
/// never has to know what a review run is. A repo-less review builds the same items with no thread and
/// nothing resolved, which is what makes one interface member serve both posting commands.
/// </remarks>
/// <param name="Content">
/// The full comment markdown. Used only when opening a thread — a follow-up reply is one of the host's
/// own fixed sentences, not this text.
/// </param>
/// <param name="Location">
/// Null when the model reported no location, or reported one that could not be parsed. Such a finding
/// is posted as a plain conversation comment rather than dropped.
/// </param>
/// <param name="ExistingThreadId">
/// The thread this finding already owns, when it has been posted before. Null opens a new one.
/// </param>
/// <param name="Resolved">Whether the finding is gone from the code, which is what closes its thread.</param>
/// <param name="Iter">The review iteration being published, quoted in a follow-up reply.</param>
/// <param name="Today">
/// The posting machine's local date, <c>yyyy-MM-dd</c>. Passed in rather than read here so every item
/// in a batch is stamped with the same day and a host stays free of a clock.
/// </param>
/// <param name="Identity">
/// What ties this item to a stored finding, or null when it matched none. Only used to link items
/// <em>within</em> one batch — see <see cref="OpenedThreads"/>.
/// </param>
internal sealed record PostItem(
    string Content,
    CommentLocation? Location,
    long? ExistingThreadId,
    bool Resolved,
    long Iter,
    string Today,
    string? Identity = null);

/// <summary>
/// The threads one batch has opened so far, so a second finding that shares an identity replies on the
/// first's thread instead of opening a duplicate.
/// </summary>
/// <remarks>
/// This exists to preserve a second-order consequence of <c>BUG-REVIEW-b</c>. CodeFlow 1.7.2 resolves
/// each item's thread from the finding list <em>as it stands at that point in its loop</em>, and
/// records a newly opened thread onto the matched finding immediately — so when two selected findings
/// collide on file and category, the second one sees the first's brand-new thread. Resolving every
/// item up front would post two threads instead, which is a different pull request.
/// </remarks>
/// <remarks>
/// An item that matched no stored finding has no identity and is never linked: 1.7.2 does not
/// record its thread either, which is the edge case where a posted comment leaves no trace.
/// </remarks>
internal sealed class OpenedThreads
{
    private readonly Dictionary<string, long> _byIdentity = new(StringComparer.Ordinal);

    /// <summary>The thread this item should continue, if any.</summary>
    public long? For(PostItem item) =>
        item.ExistingThreadId
        ?? (item.Identity is { } key && _byIdentity.TryGetValue(key, out var opened) ? opened : null);

    /// <summary>Remembers a thread this batch just opened.</summary>
    public void Record(PostItem item, long threadId)
    {
        if (item.Identity is { } key)
        {
            _byIdentity[key] = threadId;
        }
    }
}

/// <summary>What publishing one item did.</summary>
/// <remarks>
/// Three cases rather than a success flag and a nullable id: "opened a thread" and "replied on one"
/// are different enough that the caller records something for the first and nothing for the second,
/// and a failure carries a message the user reads.
/// </remarks>
internal abstract record PostOutcome
{
    private PostOutcome()
    {
    }

    /// <summary>A new thread was opened. Its id is written back onto the finding.</summary>
    public sealed record Opened(long ThreadId) : PostOutcome;

    /// <summary>A follow-up was added to a thread that already existed. Nothing to record.</summary>
    public sealed record Replied : PostOutcome;

    /// <summary>The item did not post. <paramref name="Message"/> is what the user will read.</summary>
    public sealed record Failed(string Message) : PostOutcome;
}
