namespace CodeFlow.Ai;

/// <summary>One recorded line of a run, in the shape the renderer already renders.</summary>
/// <remarks>
/// Serialised into <c>activity_log.trace</c> as a JSON array and read back by
/// <c>chatStore.ts</c>'s <c>parseTrace</c>, which expects <c>stream</c> and <c>line</c> —
/// snake_case would be identical here, but the field names are a contract either way.
/// </remarks>
public sealed record TraceLine(string Stream, string Line);

/// <summary>
/// The identity of one AI run, plus the tail of what it printed.
/// </summary>
/// <remarks>
/// <para>
/// An ambient, implicitly-propagated context would let the plumbing deep inside the AI stack
/// pick it up without threading a parameter through a dozen operation signatures.
/// Here it is an ordinary parameter instead: <see cref="AiRunRegistry.RunAsync"/>
/// already takes the run id explicitly, so an <c>AsyncLocal&lt;T&gt;</c> would add ambient state
/// without removing an argument. Same behaviour, nothing hidden.
/// </para>
/// <para>
/// A <see langword="null"/> context means an untracked run: no <c>ai:output</c> events and no stop
/// button, which is what keeps the internal auxiliary calls out of the user's activity list.
/// </para>
/// </remarks>
public sealed class AiRunContext(string runId)
{
    /// <summary>
    /// How many lines of a run are kept for its stored trace.
    /// </summary>
    /// <remarks>
    /// Enough to reconstruct what an agent did, bounded so one chatty run cannot bloat the
    /// database. The oldest lines go first: the tail is what explains how a turn ended up where it
    /// did.
    /// </remarks>
    private const int MaxTraceLines = 300;

    private readonly Lock _gate = new();
    private readonly Queue<TraceLine> _lines = new(MaxTraceLines);

    /// <summary>The id the frontend minted before invoking, so it could subscribe and hold a cancel handle.</summary>
    public string RunId { get; } = runId;

    /// <summary>Everything this run emitted, oldest first, capped at <see cref="MaxTraceLines"/>.</summary>
    public IReadOnlyList<TraceLine> Trace
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>Appends one line, dropping the oldest once the cap is reached.</summary>
    /// <remarks>Locked because the stdout and stderr pumps fill this concurrently.</remarks>
    internal void Record(string stream, string line)
    {
        lock (_gate)
        {
            if (_lines.Count >= MaxTraceLines)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(new TraceLine(stream, line));
        }
    }
}
