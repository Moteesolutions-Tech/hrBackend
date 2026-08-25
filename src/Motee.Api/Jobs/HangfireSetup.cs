using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Motee.Application.Notifications;
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
            Authorization = [new DashboardAccess(app.Environment, app.Configuration)],
            DisplayStorageConnectionString = false,
        });

        return app;
    }

    // The dashboard exposes job arguments, and this application's jobs carry rendered
    // emails - so that includes live OTP codes and one-time join links in plain text.
    // Anyone who can load the page can read them, and can requeue or delete jobs.
    // Treat the password as a real credential: it is equivalent to being able to sign
    // in as whoever is mid-registration.
    //
    // Basic auth rather than the API's own JWT scheme because this is a browser page,
    // and there is no cookie session to carry a bearer token. It is only sound over
    // TLS, which is enforced below.
    private sealed class DashboardAccess(
        IWebHostEnvironment environment,
        IConfiguration configuration) : IDashboardAuthorizationFilter
    {
        private const string Realm = "Motee jobs";

        public bool Authorize(DashboardContext context)
        {
            // A developer's own machine, where the storage is their own database.
            if (environment.IsDevelopment())
            {
                return true;
            }

            HttpContext http = context.GetHttpContext();

            // Basic auth is base64, not encryption. Over plain HTTP the password is
            // readable by anything on the path, so refuse rather than leak it.
            // UseForwardedHeaders runs earlier in the pipeline, so this reflects the
            // scheme the browser used, not the one nginx forwarded on.
            if (!http.Request.IsHttps)
            {
                return false;
            }

            string? expectedPassword = configuration["Hangfire:DashboardPassword"];

            // Fails closed. An unset password locks everyone out, which is recoverable;
            // the alternative - treating "not configured" as "no password required" -
            // publishes every OTP code to the internet.
            if (string.IsNullOrEmpty(expectedPassword))
            {
                return false;
            }

            string expectedUser = configuration["Hangfire:DashboardUser"] ?? "motee";

            if (TryReadCredentials(http, out string user, out string password)
                && FixedTimeEquals(user, expectedUser)
                && FixedTimeEquals(password, expectedPassword))
            {
                return true;
            }

            Challenge(http);

            return false;
        }

        private static bool TryReadCredentials(
            HttpContext http,
            out string user,
            out string password)
        {
            user = string.Empty;
            password = string.Empty;

            string? header = http.Request.Headers.Authorization;

            if (string.IsNullOrEmpty(header)
                || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string decoded;

            try
            {
                decoded = Encoding.UTF8.GetString(
                    Convert.FromBase64String(header["Basic ".Length..].Trim()));
            }
            catch (FormatException)
            {
                return false;
            }

            int separator = decoded.IndexOf(':', StringComparison.Ordinal);

            if (separator < 0)
            {
                return false;
            }

            user = decoded[..separator];
            password = decoded[(separator + 1)..];

            return true;
        }

        // Comparing with == would return as soon as two characters differ, so response
        // time leaks how much of the password was right - guessable one character at a
        // time. Length still differs observably, which is accepted practice.
        private static bool FixedTimeEquals(string left, string right) =>
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(left),
                Encoding.UTF8.GetBytes(right));

        // Without this the browser shows a bare 401 and never offers a login box.
        private static void Challenge(HttpContext http)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            http.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
        }
    }
}
