namespace Imperial2030.Server.Helpers;

/// <summary>
/// The body text returned to a client when a request fails with an unhandled exception.
///
/// Endpoints used to return <c>ex.Message</c> straight to the caller, which leaks whatever the exception
/// happened to carry - connection strings, file paths, EF internals. The exception itself belongs in the
/// log; the caller gets a fixed message plus the request's trace identifier so a support request can be
/// tied back to the logged entry.
/// </summary>
public static class ErrorResponses
{
    public const string GenericInternalError = "An internal error occurred.";

    /// <summary>
    /// Message for a 500 body. Pass <c>HttpContext.TraceIdentifier</c>; it is appended as a reference the
    /// user can quote, and omitted entirely when unavailable rather than rendered as an empty label.
    /// </summary>
    public static string Internal(string? traceIdentifier) =>
        string.IsNullOrWhiteSpace(traceIdentifier)
            ? GenericInternalError
            : $"{GenericInternalError} Reference: {traceIdentifier}";
}
