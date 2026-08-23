using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Motee.Application.Auth;
using Motee.Infrastructure.Notifications;

namespace Motee.Api.Jobs;

internal static class HangfireSetup
{
    public const string DashboardPath = "/jobs";

    public static IServiceCollection AddMoteeJobs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Default")!;

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));

        services.AddHangfireServer(options =>
        {
            options.WorkerCount = configuration.GetValue<int?>("Hangfire:WorkerCount") ?? 4;
        });

        services.AddScoped<SendEmailJob>();

        // Replaces the inline queue registered by AddInfrastructure.
        services.Replace(ServiceDescriptor.Scoped<IEmailQueue, HangfireEmailQueue>());

        return services;
    }

    public static WebApplication UseMoteeJobsDashboard(this WebApplication app)
    {
        app.UseHangfireDashboard(DashboardPath, new DashboardOptions
        {
            Authorization = [new DashboardAccess(app.Environment)],
            DisplayStorageConnectionString = false,
        });

        return app;
    }

    // The dashboard exposes job arguments — which include recipient addresses and
    // message bodies — and lets a visitor requeue or delete jobs. Hangfire's default
    // only blocks remote requests, so it is closed entirely outside Development
    // until it sits behind real authorisation.
    private sealed class DashboardAccess(IWebHostEnvironment environment) : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context) => environment.IsDevelopment();
    }
}
