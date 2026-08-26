using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Notifications;
using Motee.Domain.Common;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class TenantRegistrationTests(PostgresFixture fixture)
{
    private static RegisterTenantRequest Request(
        string companyName = "Acme Corporation",
        string email = "ada@acme.com",
        string countryCode = "NG") => new()
    {
        FirstName = "Ada",
        MiddleName = null,
        LastName = "Okafor",
        Email = email,
        CompanyName = companyName,
        Password = "correct-horse",
        CountryCode = countryCode,
    };

    [SkippableFact]
    public async Task CreatesTheTenantAndItsFirstAdmin()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        RegisterTenantResult result = await fixture.RegisterAsync(Request());

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));

        await using MoteeDbContext context = fixture.CreateContext();

        Tenant? tenant = await context.Tenants.FindAsync(result.TenantId);
        ApplicationUser? user = await context.Users.FindAsync(result.UserId);

        Assert.NotNull(tenant);
        Assert.NotNull(user);
        Assert.Equal("acme-corporation", tenant.Slug);
        Assert.Equal(tenant.Id, user.TenantId);
        Assert.False(user.IsPlatformStaff);
    }

    [SkippableFact]
    public async Task StoresTheJurisdictionFromTheCountryToggle()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        // The toggle sends the frontend's "uk"; the ISO code is GB.
        RegisterTenantResult result = await fixture.RegisterAsync(Request(countryCode: "uk"));

        await using MoteeDbContext context = fixture.CreateContext();
        Tenant tenant = await context.Tenants.FirstAsync(candidate => candidate.Id == result.TenantId);

        Assert.Equal(CountryCode.UnitedKingdom, tenant.CountryCode);
    }

    [SkippableFact]
    public async Task NeverStoresThePasswordInClear()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        RegisterTenantResult result = await fixture.RegisterAsync(Request());

        await using MoteeDbContext context = fixture.CreateContext();
        ApplicationUser user = await context.Users.FirstAsync(candidate => candidate.Id == result.UserId);

        Assert.NotNull(user.PasswordHash);
        Assert.DoesNotContain("correct-horse", user.PasswordHash, StringComparison.Ordinal);
    }

    // Registration must not let anyone in before the OTP is verified.
    [SkippableFact]
    public async Task LeavesTheEmailUnconfirmed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        RegisterTenantResult result = await fixture.RegisterAsync(Request());

        await using MoteeDbContext context = fixture.CreateContext();
        ApplicationUser user = await context.Users.FirstAsync(candidate => candidate.Id == result.UserId);

        Assert.False(user.EmailConfirmed);
    }

    [SkippableFact]
    public async Task GivesTheSecondCompanyOfTheSameNameADistinctSlug()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        await fixture.RegisterAsync(Request(email: "first@acme.com"));
        RegisterTenantResult second = await fixture.RegisterAsync(Request(email: "second@acme.com"));

        await using MoteeDbContext context = fixture.CreateContext();
        Tenant tenant = await context.Tenants.FirstAsync(candidate => candidate.Id == second.TenantId);

        Assert.Equal("acme-corporation-2", tenant.Slug);
    }

    // The reason registration is transactional: a tenant left behind by a failed
    // sign-up holds the unique slug, so retrying the same company name collides
    // forever. Input here is deliberately past the email column's limit — the
    // service takes pre-validated input, so this fails inside the transaction.
    [SkippableFact]
    public async Task RollsTheTenantBackWhenTheUserCannotBeCreated()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        string oversizedEmail = new string('a', 300) + "@acme.com";

        RegisterTenantResult result = await fixture.RegisterAsync(Request(email: oversizedEmail));

        Assert.False(result.Succeeded);

        await using MoteeDbContext context = fixture.CreateContext();

        Assert.Empty(await context.Tenants.ToListAsync());
        Assert.Empty(await context.Users.ToListAsync());
    }

    [SkippableFact]
    public async Task LeavesNoSlugReservedAfterAFailedAttempt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        string oversizedEmail = new string('a', 300) + "@acme.com";
        await fixture.RegisterAsync(Request(email: oversizedEmail));

        RegisterTenantResult retry = await fixture.RegisterAsync(Request());

        Assert.True(retry.Succeeded, string.Join("; ", retry.Errors));

        await using MoteeDbContext context = fixture.CreateContext();
        Tenant tenant = await context.Tenants.FirstAsync(candidate => candidate.Id == retry.TenantId);

        Assert.Equal("acme-corporation", tenant.Slug);
    }

    // One address belongs to one company, so a second sign-up with the same email
    // creates nothing - no matter which company it names.
    //
    // It does not *fail*, though. Reporting a conflict would tell an anonymous caller
    // which addresses hold accounts, so the second attempt is accepted and answered
    // exactly like the first; the owner is told by email instead. AlreadyRegistered is
    // how the caller knows not to issue a code, and is never surfaced in a response.
    [SkippableFact]
    public async Task ASecondCompanyForTheSameEmailCreatesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        RegisterTenantResult first = await fixture.RegisterAsync(Request(email: "ada@shared.com"));
        Assert.True(first.Succeeded, string.Join("; ", first.Errors));

        RegisterTenantResult second = await fixture.RegisterAsync(
            Request(companyName: "Globex Industries", email: "ada@shared.com"));

        Assert.True(second.Succeeded);
        Assert.True(second.AlreadyRegistered);

        await using MoteeDbContext context = fixture.CreateContext();

        // The company named in the second attempt must not exist. Creating it would
        // leave a tenant nobody owns and quietly consume the slug.
        Assert.Single(await context.Tenants.ToListAsync());
        Assert.Single(await context.Users.ToListAsync());
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    // One provider for the whole test, because the cooldown lives in the singleton
    // IMemoryCache — a provider per call would hand each attempt a fresh cache and the
    // throttle would never be exercised.
    private static async Task<RegisterTenantResult> RegisterAsync(
        ServiceProvider provider,
        RegisterTenantRequest request)
    {
        using IServiceScope scope = provider.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<ITenantRegistrationService>()
            .RegisterAsync(request);
    }

    // Register is anonymous and now sends mail on the duplicate path, so without a
    // cooldown it is an unauthenticated way to post a message to anyone who has an
    // account, as often as the caller likes — and the reply is identical either way, so
    // the abuse leaves no trace in the response.
    [SkippableFact]
    public async Task RepeatedDuplicateRegistrationsSendOneNotice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        RecordingEmailSender mail = new();

        await using ServiceProvider provider = fixture.BuildProvider(
            services => services.AddScoped<IEmailSender>(_ => mail));

        await RegisterAsync(provider, Request(email: "ada@shared.com"));

        int before = mail.Sent.Count;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            await RegisterAsync(provider, Request(companyName: "Globex", email: "ada@shared.com"));
        }

        Assert.Equal(1, mail.Sent.Count - before);
    }

    // Suppressing the notice must not become a way to tell the two paths apart: the
    // result has to look the same on the first attempt and the fifth.
    [SkippableFact]
    public async Task ThrottlingTheNoticeDoesNotChangeTheResult()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        await using ServiceProvider provider = fixture.BuildProvider();

        await RegisterAsync(provider, Request(email: "ada@shared.com"));

        RegisterTenantResult first = await RegisterAsync(
            provider, Request(companyName: "Globex", email: "ada@shared.com"));

        RegisterTenantResult throttled = await RegisterAsync(
            provider, Request(companyName: "Initech", email: "ada@shared.com"));

        Assert.Equal(first.Succeeded, throttled.Succeeded);
        Assert.Equal(first.AlreadyRegistered, throttled.AlreadyRegistered);
        Assert.Equal(first.Email, throttled.Email);
    }

    // The first registration is the one that creates things, and it must not be
    // mistaken for the duplicate path.
    [SkippableFact]
    public async Task AFirstRegistrationIsNotFlaggedAsAlreadyRegistered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        RegisterTenantResult first = await fixture.RegisterAsync(Request(email: "ada@shared.com"));

        Assert.True(first.Succeeded, string.Join("; ", first.Errors));
        Assert.False(first.AlreadyRegistered);
        Assert.NotEqual(Guid.Empty, first.UserId);
    }
}
