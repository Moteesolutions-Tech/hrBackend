using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Routing.Patterns;
using Motee.Api.Http;
using Motee.Application.Alerts;
using Motee.Application.Auth;

namespace Motee.Api.Contracts;

// Unhandled failures return the same envelope as everything else, so a client never
// has to parse two shapes. The exception detail is logged, not sent.
// The handler is a singleton, so nothing scoped can be injected here — IRequestContext
// is per-request and consuming it would fail service validation at startup. Everything
// needed is on the HttpContext this is handed anyway.
internal sealed class EnvelopeExceptionHandler(
    IAlertSink alerts,
    ILogger<EnvelopeExceptionHandler> logger) : IExceptionHandler
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

        await AlertAsync(httpContext, exception, cancellationToken);

        httpContext.Response.StatusCode = MoteeStatusCodes.ToHttpStatus(
            MoteeStatusCodes.InternalServerError);

        await httpContext.Response.WriteAsJsonAsync(
            ServiceResponse<object?>.Failure(
                MoteeStatusCodes.InternalServerError,
                "Something went wrong. Please try again."),
            cancellationToken);

        return true;
    }

    private async Task AlertAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!alerts.Enabled)
        {
            return;
        }

        // The route template, not the resolved path. "employees/{id}" groups every
        // failure on that endpoint together; "employees/9f3c…" would produce a distinct
        // fingerprint per record and the throttle would never engage.
        string endpoint = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
            ?? httpContext.Request.Path.ToString();
        string method = httpContext.Request.Method;

        await alerts.RaiseAsync(
            new Alert
            {
                Severity = AlertSeverity.Error,
                Title = $"{exception.GetType().Name} on {method} {endpoint}",
                Detail = Detail(exception),

                // The shape of the failure, never anything per-request. The top frame is
                // included because the same exception type thrown from two places is two
                // problems.
                Fingerprint = $"http|{method}|{endpoint}|{exception.GetType().FullName}|{TopFrame(exception)}",

                // A fixed set, assembled deliberately. No request body, no headers, no
                // query string — a Slack channel is not access-controlled, and a payload
                // in one is a data breach with a nice colour scheme.
                //
                // The correlation id is the important one: it is what turns an alert into
                // the matching line in CloudWatch.
                Facts = new Dictionary<string, string?>
                {
                    ["Endpoint"] = $"{method} {endpoint}",
                    ["Correlation id"] =
                        httpContext.Items[RequestContextKeys.CorrelationId]?.ToString(),
                    ["User"] = httpContext.User.FindFirst(MoteeClaimTypes.Subject)?.Value,
                },
            },
            cancellationToken);
    }

    // Type, message and stack, with inner exceptions unwrapped. The inner one is usually
    // the real cause — a DbUpdateException says nothing a Postgres constraint name does
    // not say better.
    private static string Detail(Exception exception)
    {
        List<string> lines = [];

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            lines.Add($"{current.GetType().Name}: {current.Message}");
        }

        if (exception.StackTrace is string stack)
        {
            lines.Add(string.Empty);
            lines.AddRange(stack.Split('\n').Take(8).Select(line => line.TrimEnd()));
        }

        return string.Join('\n', lines);
    }

    private static string TopFrame(Exception exception) =>
        exception.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "no-stack";
}
