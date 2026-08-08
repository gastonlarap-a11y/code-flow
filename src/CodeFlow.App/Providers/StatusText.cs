using System.Globalization;
using System.Net;

namespace CodeFlow.Providers;

/// <summary>
/// Renders an HTTP status and a transport failure into the error strings the UI shows.
/// </summary>
/// <remarks>
/// Shared by both provider clients, which format them identically: a status displays as
/// <c>404 Not Found</c>, and a transport failure renders the underlying error. These strings are
/// user-facing — <c>IpcServer</c> puts an exception's message straight into the JSON-RPC <c>error</c>
/// field — so a second copy of this table would be a second thing to keep in step, and the two would
/// drift the first time one of them gained a status.
/// </remarks>
internal static class StatusText
{
    /// <summary>
    /// A status as 1.7.2 prints it: the code, then its canonical reason phrase.
    /// </summary>
    /// <remarks>
    /// The reason comes from the code, not from <see cref="HttpResponseMessage.ReasonPhrase"/>, which the
    /// server supplies and can be anything. A status with no known reason degrades to the bare number —
    /// a visibly incomplete message rather than a wrong one.
    /// </remarks>
    public static string Of(HttpStatusCode status) =>
        ReasonPhrases.TryGetValue(status, out var reason)
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)status} {reason}")
            : ((int)status).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The innermost message of a failure, which is what 1.7.2's <c>{e}</c> renders.
    /// </summary>
    /// <remarks>
    /// .NET wraps a socket error in an <see cref="HttpRequestException"/> whose own message is generic
    /// ("An error occurred while sending the request"), so the useful text is one level down.
    /// </remarks>
    public static string Reason(Exception failure)
    {
        var innermost = failure;
        while (innermost.InnerException is { } inner)
        {
            innermost = inner;
        }

        return innermost.Message;
    }

    /// <summary>
    /// The canonical reason phrases for the statuses these clients can actually see.
    /// </summary>
    /// <remarks>
    /// Hand-written because .NET exposes no canonical-reason table. Covers what GitHub and Azure DevOps
    /// document for these endpoints plus the transport-level ones a proxy can inject.
    /// </remarks>
    private static readonly Dictionary<HttpStatusCode, string> ReasonPhrases = new()
    {
        [HttpStatusCode.BadRequest] = "Bad Request",
        [HttpStatusCode.Unauthorized] = "Unauthorized",
        [HttpStatusCode.Forbidden] = "Forbidden",
        [HttpStatusCode.NotFound] = "Not Found",
        [HttpStatusCode.MethodNotAllowed] = "Method Not Allowed",
        [HttpStatusCode.NotAcceptable] = "Not Acceptable",
        [HttpStatusCode.Conflict] = "Conflict",
        [HttpStatusCode.Gone] = "Gone",
        [HttpStatusCode.UnprocessableEntity] = "Unprocessable Entity",
        [HttpStatusCode.TooManyRequests] = "Too Many Requests",
        [HttpStatusCode.InternalServerError] = "Internal Server Error",
        [HttpStatusCode.NotImplemented] = "Not Implemented",
        [HttpStatusCode.BadGateway] = "Bad Gateway",
        [HttpStatusCode.ServiceUnavailable] = "Service Unavailable",
        [HttpStatusCode.GatewayTimeout] = "Gateway Timeout",
    };
}
