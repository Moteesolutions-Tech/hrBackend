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
}
