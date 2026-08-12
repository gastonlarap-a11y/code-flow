namespace CodeFlow.Platform;

/// <summary>
/// Sends a request a second time when the first attempt never left the machine.
/// </summary>
/// <remarks>
/// <para>
/// Placed in the one <see cref="HttpClient"/> the process owns, so every provider call is covered
/// without any client knowing this exists. A brief loss of name resolution is otherwise fatal to
/// whatever the user was in the middle of: on 2026-08-12 it cost two work-item reads and, ninety
/// seconds later, an entire AI review.
/// </para>
/// <para>
/// <b>Only requests with no body.</b> That restriction is doing two jobs at once. It keeps this to
/// the reads — a <c>GET</c> repeated is a <c>GET</c> — so nothing here can turn one posted comment,
/// one approval or one pull request into two. And it makes the repeat trivially constructible: a
/// request with no content is fully described by its method, its URI, its version and its headers,
/// so the second attempt is a fresh message rather than a reused one whose content stream may
/// already be spent.
/// </para>
/// </remarks>
internal sealed class TransientRetryHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failure)
            when (request.Content is null && TransientNetwork.Caused(failure))
        {
            // Immediately, and once. A resolver that is coming back is back within the time it takes
            // to build this message; one that is not will fail again and the caller hears the same
            // reason it would have heard anyway, a few milliseconds later.
            return await base.SendAsync(Repeat(request), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A fresh message carrying everything the original said.</summary>
    private static HttpRequestMessage Repeat(HttpRequestMessage original)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };

        // Without validation: these headers already passed it once on their way into the original,
        // and re-validating an `Authorization` or a custom `X-TFS-…` here could reject what the
        // provider clients deliberately set by hand.
        foreach (var (name, values) in original.Headers)
        {
            copy.Headers.TryAddWithoutValidation(name, values);
        }

        return copy;
    }
}
