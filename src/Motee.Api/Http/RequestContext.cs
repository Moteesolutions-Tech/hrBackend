using Motee.Application.Auth;
using Motee.Application.Common;

namespace Motee.Api.Http;

internal sealed class RequestContext(IHttpContextAccessor httpContextAccessor) : IRequestContext
{
    private HttpContext? Context => httpContextAccessor.HttpContext;

    public string? RequestId => Context?.Items[RequestContextKeys.RequestId]?.ToString();

    public string? CorrelationId => Context?.Items[RequestContextKeys.CorrelationId]?.ToString();

    public string? UserId => Context?.User.FindFirst(MoteeClaimTypes.Subject)?.Value;

    public string? UserEmail => Context?.User.FindFirst(MoteeClaimTypes.Email)?.Value;

    public string? IpAddress => Context?.Items[RequestContextKeys.IpAddress]?.ToString();

    public string? UserAgent => Context?.Request.Headers.UserAgent.ToString();

    // The route template when routing has resolved one, so every request to a given
    // endpoint shares a value the audit screen can group and filter by. Falls back to
    // the raw path for anything unrouted — a 404, or middleware running before the
    // endpoint is selected.
    public string? Endpoint =>
        (Context?.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
        ?? Context?.Request.Path.Value;

    public string? HttpMethod => Context?.Request.Method;
}
