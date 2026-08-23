using Microsoft.AspNetCore.Diagnostics;

namespace Motee.Api.Contracts;

// Unhandled failures return the same envelope as everything else, so a client never
// has to parse two shapes. The exception detail is logged, not sent.
internal sealed class EnvelopeExceptionHandler(ILogger<EnvelopeExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            exception,
            "Unhandled exception for {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        httpContext.Response.StatusCode = MoteeStatusCodes.ToHttpStatus(
            MoteeStatusCodes.InternalServerError);

        await httpContext.Response.WriteAsJsonAsync(
            ServiceResponse<object?>.Failure(
                MoteeStatusCodes.InternalServerError,
                "Something went wrong. Please try again."),
            cancellationToken);

        return true;
    }
}
