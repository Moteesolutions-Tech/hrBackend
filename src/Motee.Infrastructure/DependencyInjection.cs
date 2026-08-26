using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Identity;
using Motee.Application.Auth;
using Motee.Application.Assets;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Exports;
using Motee.Application.Files;
using Motee.Application.Notifications;
using Motee.Application.Organisation;
using Motee.Application.Tenancy;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Assets;
using Motee.Infrastructure.Employees;
using Motee.Infrastructure.Exports;
using Motee.Infrastructure.Files;
using Motee.Infrastructure.Organisation;
using Motee.Infrastructure.Auth;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Notifications;
using Motee.Infrastructure.Notifications.Templates;
using Motee.Infrastructure.Persistence;
using Motee.Infrastructure.Tenancy;

namespace Motee.Infrastructure;

/// <summary>
/// Registers infrastructure concerns (database, external services) with the DI container,
/// so the API project never has to reference the database provider directly.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "Connection string 'Default' was not found. Set ConnectionStrings:Default in "
                + "appsettings.Development.json or via 'dotnet user-secrets set'.");

        services.AddDbContext<MoteeDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantSlugGenerator, TenantSlugGenerator>();
        services.AddScoped<Authorization.AccessLevelSeeder>();
        services.AddMemoryCache();
        services.AddScoped<IUserPermissions, Authorization.UserPermissions>();
        services.AddScoped<Application.Authorization.IAccessLevelService, Authorization.AccessLevelService>();
        services.AddScoped<Application.Authorization.ITenantUserService, Authorization.TenantUserService>();
        services.AddScoped<ITenantRegistrationService, TenantRegistrationService>();
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<IUserLookup, UserLookup>();
        services.AddScoped<IAccessTokenService, AccessTokenService>();
        services.AddScoped<ILoginService, LoginService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<ISessionIssuer, SessionIssuer>();
        services.AddScoped<IDepartmentService, DepartmentService>();
        services.AddScoped<IBusinessUnitService, BusinessUnitService>();
        services.AddScoped<IAssetService, AssetService>();
        services.AddScoped<AvatarLinker>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<IEmployeeImportService, EmployeeImportService>();
        services.AddScoped<IEmployeeInvitationService, EmployeeInvitationService>();
        services.AddScoped<IExportService, ExportService>();
        services.AddScoped<IExportRunner, ExportRunner>();
        services.AddScoped<IFileUploadService, FileUploadService>();
        services.AddSingleton<AppLinks>();

        AddFileStorage(services, configuration);

        // Hangfire is hosted by the API; anything else runs the export inline.
        services.TryAddScoped<IExportQueue, InlineExportQueue>();
        services.AddScoped<ITenantSetupService, TenantSetupService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IDebugMode, DebugMode>();

        AddEmailProvider(services, configuration);
        AddEmailTemplates(services);

        // Hangfire is hosted by the API; anything else (tests, tooling) sends inline.
        services.TryAddScoped<IEmailQueue, InlineEmailQueue>();

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                // The sign-up form promises "Min. 8 chars" and nothing else;
                // Identity's defaults would reject passwords the form accepted.
                options.Password.RequiredLength = RegisterTenantValidator.MinimumPasswordLength;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                // One address belongs to one company; Identity's global check is
                // exactly right, backed by the unique index on users.
                options.User.RequireUniqueEmail = true;

                options.SignIn.RequireConfirmedEmail = true;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<MoteeDbContext>()
            // Generates the 6-digit codes the verify-otp and password-reset screens
            // expect. AddDefaultTokenProviders lives in the ASP.NET Core framework
            // assembly, which this class library does not reference.
            .AddTokenProvider<EmailTokenProvider<ApplicationUser>>(TokenOptions.DefaultEmailProvider);

        // Must come after AddIdentityCore, which registers the username-based default.
        services.RemoveAll<IUserValidator<ApplicationUser>>();
        services.AddScoped<IUserValidator<ApplicationUser>, EmailOnlyUserValidator>();


        return services;
    }

    // S3 only. Local disk was tempting for development, but two ECS tasks would each
    // write to their own volume and half the downloads would 404 — a bug that only
    // appears once it is deployed. Tests register their own in-memory storage.
    //
    // With no bucket configured the AWS client is not registered at all. Building one
    // resolves credentials there and then, and storage is now a dependency of the
    // employee and invitation services — so a missing key took down every employee and
    // join request, not merely the uploads.
    private static void AddFileStorage(IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Storage:Bucket"]))
        {
            services.TryAddScoped<IFileStorage, UnconfiguredFileStorage>();
            return;
        }

        string? serviceUrl = configuration["Storage:ServiceUrl"];

        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            // S3 itself. Credentials come from the environment — on ECS that is the
            // task role, so there are no keys to store anywhere.
            services.AddAWSService<Amazon.S3.IAmazonS3>();
        }
        else
        {
            AddCompatibleStorage(services, configuration, serviceUrl);
        }

        services.TryAddScoped<IFileStorage, S3FileStorage>();
    }

    // Cloudflare R2, Backblaze B2, MinIO — anything that speaks the S3 API at its own
    // address. Two settings make the difference: the endpoint, and path-style
    // addressing, because bucket-as-subdomain only resolves against Amazon's domains.
    private static void AddCompatibleStorage(
        IServiceCollection services,
        IConfiguration configuration,
        string serviceUrl)
    {
        string accessKey = configuration["Storage:AccessKey"]
            ?? throw new InvalidOperationException(
                "Storage:ServiceUrl is set, so Storage:AccessKey is required. The AWS "
                + "credential chain does not apply to a non-Amazon endpoint.");

        string secretKey = configuration["Storage:SecretKey"]
            ?? throw new InvalidOperationException(
                "Storage:ServiceUrl is set, so Storage:SecretKey is required.");

        // Signing still needs a region even where the store has no regions of its
        // own. R2 expects "auto".
        string region = configuration["Storage:Region"] ?? "auto";

        // Singleton: the client is thread-safe, holds its connection pool, and is
        // built for reuse. It is also why an unreachable endpoint surfaces on first
        // use rather than at startup.
        services.AddSingleton<Amazon.S3.IAmazonS3>(_ => new Amazon.S3.AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials(accessKey, secretKey),
            new Amazon.S3.AmazonS3Config
            {
                ServiceURL = serviceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = region,
            }));
    }

    // The list of every email the system can send. Adding one is a model record, a
    // template class, and a line here - no business service changes, and nothing else
    // in the codebase has to know the wording exists.
    private static void AddEmailTemplates(IServiceCollection services)
    {
        services.AddScoped<IEmailDispatcher, EmailDispatcher>();

        services.AddScoped<IEmailTemplate<OtpCodeEmail>, OtpCodeEmailTemplate>();
        services.AddScoped<
            IEmailTemplate<AccountAlreadyExistsEmail>, AccountAlreadyExistsEmailTemplate>();
        services.AddScoped<IEmailTemplate<ExportReadyEmail>, ExportReadyEmailTemplate>();
        services.AddScoped<IEmailTemplate<EmployeeInviteEmail>, EmployeeInviteEmailTemplate>();
    }

    private static void AddEmailProvider(IServiceCollection services, IConfiguration configuration)
    {
        string provider = configuration["Email:Provider"]?.Trim().ToLowerInvariant() ?? "none";

        switch (provider)
        {
            case "resend":
                services.AddHttpClient<IEmailSender, ResendEmailSender>(client =>
                {
                    client.BaseAddress = new Uri("https://api.resend.com/");
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                        "Bearer",
                        configuration["Resend:ApiKey"]
                            ?? throw new InvalidOperationException(
                                "Email:Provider is 'resend' but Resend:ApiKey is not configured."));
                });
                break;

            default:
                // TryAdd so a caller that registered its own sender first — tests,
                // tooling — keeps it rather than being silently replaced.
                services.TryAddScoped<IEmailSender, NoopEmailSender>();
                break;
        }
    }
}
