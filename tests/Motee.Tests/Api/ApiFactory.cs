using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Motee.Application.Auth;

namespace Motee.Tests.Api;

// Hosts the real pipeline — routing, the permission handler, the controllers — so
// tests can reach the layer where every authorization bug in this project has
// actually been.
//
// The service tests below this were all passing while /auth/me returned an empty
// permission matrix to every user and the medical gate refused the account owner.
// Both were controller code calling a resolver that had moved.
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    // Set per test, before the request. The handler reads it, so a test can act as a
    // particular user without minting a real JWT — the token path is covered by the
    // session tests, and what these exercise is what happens after it.
    public Guid? ActingAs { get; set; }

    // Needed by anything the tenant query filter touches. /auth/me resolves the user
    // directly and works without it; every tenant-scoped table does not, and its
    // absence presents as a 404 rather than as a missing claim.
    public Guid? ActingTenant { get; set; }

    // Anything logged at Error, so an opaque 500 can say what actually threw.
    public List<string> Failures { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        // UseSetting, not ConfigureAppConfiguration. The factory roots the host at the
        // API project, so its appsettings.json loads — and that file points at the
        // developer's own motee_dev. An in-memory source added afterwards did not
        // reliably win, and the tests silently ran against real data until a missing
        // column gave it away. Host configuration takes precedence, so this cannot.
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting(
            "JwtSettings:SigningKey", "test-signing-key-long-enough-for-hmac-sha256-abcdef");
        builder.UseSetting("App:BaseUrl", "https://app.test");
        builder.UseSetting("Cors:Origins:0", "https://tests.local");

        // Nothing here should reach S3, and an unset bucket keeps the AWS client from
        // being constructed at all.
        builder.UseSetting("Storage:Bucket", string.Empty);

        // The host is Development so the pipeline behaves normally, but the API's own
        // appsettings.json turns App:Debug on - which would leave every test running
        // with the developer shortcuts enabled and hide exactly the exposures these
        // are here to catch. Off, matching what PostgresFixture does for the same
        // reason.
        builder.UseSetting("App:Debug", "false");

        // The envelope deliberately hides exception detail from callers, which is right
        // in production and useless in a test. Kept here so a 500 can be explained.
        builder.ConfigureLogging(logging => logging
            .ClearProviders()
            .AddProvider(new CapturingLoggerProvider(Failures)));

        builder.ConfigureTestServices(services =>
        {
            // Replaces the JWT scheme with one that trusts ActingAs. Authentication is
            // not what is under test here; authorization is.
            services.AddAuthentication(TestScheme.Name)
                .AddScheme<AuthenticationSchemeOptions, TestScheme>(TestScheme.Name, _ => { });

            services.AddSingleton(this);
        });
    }

    private sealed class CapturingLoggerProvider(List<string> failures) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capturing(failures);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<string> failures) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error)
                {
                    return;
                }

                lock (failures)
                {
                    failures.Add($"{formatter(state, exception)} {exception}");
                }
            }
        }
    }

    internal sealed class TestScheme(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiFactory factory) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (factory.ActingAs is not Guid userId)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            // Deliberately no role claim: the point of these tests is that nothing
            // downstream still derives permissions from one.
            List<Claim> claims = [new Claim(MoteeClaimTypes.Subject, userId.ToString())];

            if (factory.ActingTenant is Guid tenantId)
            {
                claims.Add(new Claim(MoteeClaimTypes.TenantId, tenantId.ToString()));
            }

            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Name));

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Name)));
        }
    }
}
