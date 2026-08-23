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
        });

        return app;
    }
}
