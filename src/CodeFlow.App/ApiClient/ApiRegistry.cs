using System.Collections.Concurrent;

namespace CodeFlow.ApiClient;

/// <summary>
/// The in-flight requests a caller can still cancel (<c>API-060</c>, <c>API-061</c>).
/// </summary>
/// <remarks>
/// <para>
/// Only <c>api_send_http_tracked</c> registers: a plain <c>api_send_http</c> has no id, so nothing
/// could name it to cancel it. The renderer mints the id before sending, which is what lets the
/// stop button work before the response has begun.
/// </para>
/// <para>
/// Cancelling an id nobody registered is deliberately not an error. The request may have finished
/// between the user pressing stop and the command arriving, and that race is the normal case rather
/// than a fault.
/// </para>
/// </remarks>
public sealed class ApiRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Registers an id and answers the token its request should run under.</summary>
    /// <remarks>
    /// A repeated id replaces the earlier registration and cancels it: the renderer reusing an id
    /// means it considers the first send superseded.
    /// </remarks>
    public CancellationTokenSource Track(string id, CancellationToken linked)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(linked);

        if (_inFlight.TryRemove(id, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        _inFlight[id] = source;

        return source;
    }

    /// <summary>Forgets an id once its request has finished, however it finished.</summary>
    public void Release(string id, CancellationTokenSource source)
    {
        // Only if it is still ours: a replacement registration under the same id must survive.
        if (_inFlight.TryGetValue(id, out var current) && ReferenceEquals(current, source))
        {
            _inFlight.TryRemove(id, out _);
        }

        source.Dispose();
    }

    /// <summary>Cancels one in-flight request. A no-op when there is none.</summary>
    public void Cancel(string id)
    {
        if (_inFlight.TryRemove(id, out var source))
        {
            source.Cancel();
        }
    }

    public void Dispose()
    {
        foreach (var id in _inFlight.Keys)
        {
            if (_inFlight.TryRemove(id, out var source))
            {
                source.Dispose();
            }
        }
    }
}
