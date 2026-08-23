using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Motee.Application.Auth;
using Motee.Domain.Auth;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class OtpDebugModeTests(PostgresFixture fixture)
{
    private const string FixedCode = "123456";

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Motee.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static RegisterTenantRequest Request() => new()
    {
        FirstName = "Ada",
        LastName = "Okafor",
        Email = "ada@acme.com",
        CompanyName = "Acme Corporation",
        Password = "correct-horse",
        CountryCode = "NG",
    };

    private async Task<(Guid UserId, RecordingEmailSender Mail, ServiceProvider Provider)> ArrangeAsync(
        bool appDebug,
        string environmentName)
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(Request());
        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        RecordingEmailSender mail = new();

        ServiceProvider provider = fixture.BuildProvider(services =>
        {
            services.AddScoped<IEmailSender>(_ => mail);
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(environmentName));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = fixture.ConnectionString,
                    ["JwtSettings:SigningKey"] = "test-signing-key-long-enough-for-hmac-sha256-abcdef",
                    ["App:Debug"] = appDebug.ToString(),
                })
                .Build());
        });

        return (registration.UserId, mail, provider);
    }

    private static IOtpService Otp(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IOtpService>();

    [SkippableFact]
    public async Task TheFixedCodeVerifiesWhenDebugIsOn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, _, ServiceProvider provider) =
            await ArrangeAsync(appDebug: true, Environments.Development);
        await using ServiceProvider owned = provider;

        await Otp(owned).IssueAsync(userId, OtpPurpose.EmailVerification);

        OtpAttemptOutcome outcome =
            await Otp(owned).VerifyAsync(userId, OtpPurpose.EmailVerification, FixedCode);

        Assert.Equal(OtpAttemptOutcome.Verified, outcome);
    }

    // There is nothing to deliver when the code is a known constant.
    [SkippableFact]
    public async Task NoEmailIsQueuedWhenDebugIsOn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) =
            await ArrangeAsync(appDebug: true, Environments.Development);
        await using ServiceProvider owned = provider;

        await Otp(owned).IssueAsync(userId, OtpPurpose.EmailVerification);

        Assert.Empty(mail.Sent);
    }

    // Only the comparison is relaxed; the rest of the policy still runs.
    [SkippableFact]
    public async Task AWrongCodeIsStillRejectedWhenDebugIsOn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, _, ServiceProvider provider) =
            await ArrangeAsync(appDebug: true, Environments.Development);
        await using ServiceProvider owned = provider;

        await Otp(owned).IssueAsync(userId, OtpPurpose.EmailVerification);

        Assert.Equal(
            OtpAttemptOutcome.IncorrectCode,
            await Otp(owned).VerifyAsync(userId, OtpPurpose.EmailVerification, "999999"));
    }

    [SkippableFact]
    public async Task TheFixedCodeIsSingleUseLikeAnyOther()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, _, ServiceProvider provider) =
            await ArrangeAsync(appDebug: true, Environments.Development);
        await using ServiceProvider owned = provider;

        await Otp(owned).IssueAsync(userId, OtpPurpose.EmailVerification);
        await Otp(owned).VerifyAsync(userId, OtpPurpose.EmailVerification, FixedCode);

        Assert.Equal(
            OtpAttemptOutcome.AlreadyUsed,
            await Otp(owned).VerifyAsync(userId, OtpPurpose.EmailVerification, FixedCode));
    }

    [SkippableFact]
    public async Task DebugIsOffByDefault()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) =
            await ArrangeAsync(appDebug: false, Environments.Development);
        await using ServiceProvider owned = provider;

        await Otp(owned).IssueAsync(userId, OtpPurpose.EmailVerification);

        Assert.Single(mail.Sent);
        Assert.Equal(
            OtpAttemptOutcome.IncorrectCode,
            await Otp(owned).VerifyAsync(userId, OtpPurpose.EmailVerification, FixedCode));
    }

    // The safeguard that kedco's version lacks: App:Debug is a plain config flag, so
    // one stray environment variable would otherwise make "123456" open every
    // account in production.
    [SkippableFact]
    public async Task DebugIsIgnoredInProductionEvenWhenTheFlagIsSet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) =
            await ArrangeAsync(appDebug: true, Environments.Production);
        await using ServiceProvider owned = provider;

        await Otp(owned).IssueAsync(userId, OtpPurpose.EmailVerification);

        Assert.Single(mail.Sent);
        Assert.Equal(
            OtpAttemptOutcome.IncorrectCode,
            await Otp(owned).VerifyAsync(userId, OtpPurpose.EmailVerification, FixedCode));
    }
}
