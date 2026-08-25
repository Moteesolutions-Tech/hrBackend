using Serilog;
using Serilog.Events;

namespace Motee.Api.Logging;

internal static class LoggingSetup
{
    public static IHostApplicationBuilder AddMoteeLogging(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, configuration) => configuration
            .MinimumLevel.Information()
            // Framework chatter stays quiet; the per-request line comes from Serilog's
            // own logger, so it survives this override.
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "motee-api")
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
            // Last, so a "Serilog" section in appsettings overrides the defaults above.
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services));

        return builder;
    }

    public static IApplicationBuilder UseMoteeRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";

            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("ClientIp", httpContext.Items[Http.RequestContextKeys.IpAddress]);
                diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
                diagnosticContext.Set("TenantId", httpContext.User.FindFirst("tenant_id")?.Value);
            };

            options.GetLevel = (httpContext, _, exception) =>
            {
                if (exception is not null || httpContext.Response.StatusCode >= 500)
                {
                    return LogEventLevel.Error;
                }

                // 4xx still logs, at Warning. A 401 on the jobs dashboard is somebody
                // failing to authenticate, which is exactly the traffic worth seeing.
                if (httpContext.Response.StatusCode >= 400)
                {
                    return LogEventLevel.Warning;
                }

                // Verbose is below the configured minimum, so these are dropped rather
                // than written. Only successful requests qualify - a failing health
                // check or a broken dashboard still appears above.
                return IsRoutine(httpContext.Request.Path)
                    ? LogEventLevel.Verbose
                    : LogEventLevel.Information;
            };
        });

        return app;
    }

    // Traffic that says nothing when it succeeds, and arrives constantly.
    //
    // The jobs dashboard polls /jobs/stats every two seconds for as long as a tab is
    // open, and the container health check runs every thirty. Left at Information they
    // are the overwhelming majority of the log, which costs money to ingest and buries
    // the lines that matter - the entire point of having logs.
    private static bool IsRoutine(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/jobs", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase);
}
