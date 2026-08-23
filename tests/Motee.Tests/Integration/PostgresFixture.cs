using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application;
using Motee.Application.Auth;
using Motee.Application.Tenancy;
using Motee.Infrastructure;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Connects to a database you have already created and never applies schema.
// Opt-in: without MOTEE_TEST_CONNECTION set, integration tests skip rather than
// fail, so the suite still runs on a machine with no database.
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "MOTEE_TEST_CONNECTION";

    public string? SkipReason { get; private set; }

    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable(ConnectionVariable);

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            SkipReason =
                $"Set {ConnectionVariable} to a Postgres connection string to run integration tests. "
                + "Point it at a scratch database — these tests delete rows.";
            return;
        }

        try
        {
            await using MoteeDbContext context = CreateContext();

            if (!await context.Database.CanConnectAsync())
            {
                SkipReason = $"{ConnectionVariable} is set but the database is unreachable.";
                return;
            }

            // Fail loudly rather than skipping if the schema has not been applied —
            // that is a real problem, not an absent opt-in.
            await context.Tenants.AnyAsync();
        }
        catch (Exception exception)
        {
            SkipReason = $"{ConnectionVariable} is set but unusable: {exception.Message}";
        }
    }

    // Tenant-scoped entities are filtered by the context, so a verification query
    // has to say which tenant it is asking as. Passing null sees no scoped rows at
    // all, which is what an unauthenticated context should see.
    public MoteeDbContext CreateContext(Guid? tenantId = null)
    {
        DbContextOptions<MoteeDbContext> options = new DbContextOptionsBuilder<MoteeDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new MoteeDbContext(options, new FixedTenant(tenantId));
    }

    internal sealed class FixedTenant(Guid? tenantId) : ICurrentTenant
    {
        public Guid? TenantId { get; } = tenantId;
    }

    // Settable so a test can act as one tenant, then another, against the same
    // service provider.
    public sealed class MutableTenant : ICurrentTenant
    {
        private Guid? _tenantId;

        // Mirrors the API's CurrentTenant: an explicit tenant wins, and with none set
        // it falls back to whatever a background job or a joiner made ambient. Without
        // this the double is more capable than production and hides the case where
        // AmbientTenant is the only thing resolving a tenant at all.
        public Guid? TenantId
        {
            get => _tenantId ?? AmbientTenant.TenantId;
            set => _tenantId = value;
        }
    }

    // Exercises the real DI graph — UserManager, password hasher, slug generator
    // and the transaction — rather than a hand-assembled service.
    public ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();

        services.AddLogging();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["JwtSettings:SigningKey"] = "test-signing-key-long-enough-for-hmac-sha256-abcdef",
                ["JwtSettings:AccessTokenMinutes"] = "15",
                ["App:BaseUrl"] = "https://app.test",
            })
            .Build();

        services.AddSingleton(configuration);

        // OtpDebugMode needs an environment. Named Development but with App:Debug
        // unset, so the fixed-code path stays off and tests exercise real Identity
        // token generation and verification.
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());

        // S3 is the only storage in production code, so tests supply their own
        // rather than reaching for AWS credentials.
        services.AddSingleton<InMemoryFileStorage>();
        services.AddSingleton<Motee.Application.Exports.IFileStorage>(
            provider => provider.GetRequiredService<InMemoryFileStorage>());

        // Request-scoped in the API; tests stand in for it so services that record
        // who asked can be exercised without an HTTP context.
        services.AddSingleton<StubRequestContext>();
        services.AddSingleton<Motee.Application.Common.IRequestContext>(
            provider => provider.GetRequiredService<StubRequestContext>());

        services.AddSingleton<MutableTenant>();
        services.AddSingleton<ICurrentTenant>(provider => provider.GetRequiredService<MutableTenant>());

        // Applied before AddInfrastructure so TryAdd registrations defer to it.
        configure?.Invoke(services);

        services.AddApplication();
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    public sealed class StubRequestContext : Motee.Application.Common.IRequestContext
    {
        public string? RequestId => "test-request";

        public string? CorrelationId => "test-correlation";

        // Populated by default: production always has a signed-in user behind
        // [Authorize], and services that check ownership fail closed without one.
        public string? UserId { get; set; } = Guid.NewGuid().ToString();

        public string? UserEmail { get; set; } = "tester@acme.com";

        public string? IpAddress => null;

        public string? UserAgent => "Motee.Tests";
    }

    public sealed class InMemoryFileStorage : Motee.Application.Exports.IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Keys => _files.Keys;

        public async Task SaveAsync(
            string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using MemoryStream buffer = new();
            await content.CopyToAsync(buffer, cancellationToken);
            _files[key] = buffer.ToArray();
        }

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(
                _files.TryGetValue(key, out byte[]? bytes) ? new MemoryStream(bytes) : null);

        // Shaped like a presigned URL so tests can assert on expiry and file name
        // without reaching S3.
        public Task<string> GetDownloadUrlAsync(
            string key,
            TimeSpan validFor,
            string? downloadFileName = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                $"https://storage.test/{key}?expires={(int)validFor.TotalSeconds}"
                + $"&filename={downloadFileName}");

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _files.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Motee.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    public async Task<RegisterTenantResult> RegisterAsync(RegisterTenantRequest request)
    {
        await using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<ITenantRegistrationService>()
            .RegisterAsync(request);
    }

    public async Task ResetAsync()
    {
        await using MoteeDbContext context = CreateContext();

        await context.Database.ExecuteSqlRawAsync(
            // Listed explicitly rather than relying on the cascade from tenants. The
            // cascade does clear them today, but a table added later without a tenant
            // foreign key would leak rows between tests, and the failure would look
            // like a flaky assertion rather than a missing name here.
            "TRUNCATE user_access_levels, access_levels, business_units, assets, stored_files, "
            + "export_jobs, employee_invitations, employee_bank_details, "
            + "employee_identity_documents, employee_medical, employees, departments, users, "
            + "tenants RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
