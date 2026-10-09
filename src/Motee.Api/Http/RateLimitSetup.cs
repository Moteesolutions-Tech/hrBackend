using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Motee.Api.Contracts;

namespace Motee.Api.Http;

internal static class RateLimitSetup
{
    // Applied to the anonymous auth endpoints. Everything behind a token is already
    // bounded by having to hold one, and by the permission checks after it.
    public const string AuthPolicy = "auth";

    // The two anonymous endpoints that cost something real. Registering creates a
    // tenant, eight access levels and a password hash before sending mail; forgotten
    // password sends mail to an address the caller names. One limit covering both those
    // and a lookup like verify-otp is wrong at one end or the other.
    public const string ExpensiveAuthPolicy = "auth-expensive";

    public static IServiceCollection AddMoteeRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Deliberately generous. Partitioning by IP is the only option before model
        // binding, and mobile networks in the countries this serves put whole regions
        // behind a handful of carrier-grade NAT addresses — so a tight per-IP limit
        // locks out an office or a city rather than an attacker. This exists to stop
        // volume, not to be the only control: the OTP resend cooldown and Identity's
        // account lockout are what bound per-account abuse.
        int permitPerMinute = configuration.GetValue<int?>("RateLimit:AuthPerMinute") ?? 20;

        // A company registers once, and a person forgets their password rarely, so this
        // can be far tighter than the general limit without touching normal use — while
        // still stopping someone creating tenants or sending mail in bulk.
        int expensivePerWindow = configuration.GetValue<int?>("RateLimit:ExpensiveAuthPer5Min") ?? 10;

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(AuthPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ClientKey(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitPerMinute,
                        Window = TimeSpan.FromMinutes(1),

                        // No queue. Holding a request to admit it later means a caller
                        // being throttled still occupies a connection, which is the
                        // resource the limit is protecting.
                        QueueLimit = 0,
                    }));

            options.AddPolicy(ExpensiveAuthPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ClientKey(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = expensivePerWindow,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0,
                    }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json";

                // Clients already read Retry-After from the OTP resend path, so the
                // header means the same thing everywhere it appears.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                // The envelope every other response uses. A bare 429 from the
                // middleware would be the one reply the frontend cannot parse.
                await context.HttpContext.Response.WriteAsync(
                    JsonSerializer.Serialize(
                        ServiceResponse<object?>.Failure(
                            MoteeStatusCodes.TooManyRequests,
                            "Too many requests. Wait a moment and try again."),
                        JsonOptions),
                    cancellationToken);
            };
        });

        return services;
    }

    // The resolved client address, not the socket's — ForwardedHeaders has already run,
    // so behind nginx this is the visitor rather than the proxy. Reading the connection
    // directly is the mistake that makes every caller share one partition, turning a
    // per-client limit into a global one that the first burst exhausts for everybody.
    private static string ClientKey(HttpContext context) =>
        context.Items[RequestContextKeys.IpAddress] as string
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}
