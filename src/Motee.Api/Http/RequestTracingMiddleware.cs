using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Serilog.Context;

namespace Motee.Api.Http;

internal sealed class RequestTracingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        string requestId = context.Request.Headers["X-Request-Id"].FirstOrDefault()
            ?? context.TraceIdentifier;

        string correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? requestId;

        string? clientIp = ResolveClientIp(context);

        context.Items[RequestContextKeys.RequestId] = requestId;
        context.Items[RequestContextKeys.CorrelationId] = correlationId;
        context.Items[RequestContextKeys.IpAddress] = clientIp;

        context.Response.Headers["X-Request-Id"] = requestId;
        context.Response.Headers["X-Correlation-Id"] = correlationId;

        Activity.Current?.SetTag("request_id", requestId);
        Activity.Current?.SetTag("correlation_id", correlationId);

        using (LogContext.PushProperty("RequestId", requestId))
        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("ClientIp", clientIp))
        {
            await next(context);
        }
    }

    // RemoteIpAddress is already the real client when ForwardedHeaders is configured
    // with trusted proxies. IPv4-mapped IPv6 (::ffff:41.58.x.x) is unwrapped so audit
    // rows and allowlists compare consistently.
    private static string? ResolveClientIp(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;

        if (address is null)
        {
            return null;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }
}
