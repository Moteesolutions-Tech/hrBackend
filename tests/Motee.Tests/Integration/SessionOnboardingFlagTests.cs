using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Tenancy;
using Motee.Domain.Auth;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The client has to decide where to route the instant a session is issued. Without
// the flag on the session itself it routes to the dashboard, then learns from /me
// that setup is unfinished and bounces — a visible flash of the wrong screen.
[Collection(PostgresCollection.Name)]
public class SessionOnboardingFlagTests(PostgresFixture fixture)
{
    private const string Email = "ada@acme.com";
    private const string Password = "correct-horse";

    private async Task<(Guid UserId, Guid TenantId, ServiceProvider Provider)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = Email,
            CompanyName = "Acme Corporation",
            Password = Password,
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        ServiceProvider provider = fixture.BuildProvider();

        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOtpService>()
                .IssueAsync(registration.UserId, OtpPurpose.EmailVerification);
        }

        return (registration.UserId, registration.TenantId, provider);
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static Task<LoginResult> LoginAsync(ServiceProvider provider) =>
        Resolve<ILoginService>(provider)
            .LoginAsync(new LoginRequest { Email = Email, Password = Password });

    [SkippableFact]
    public async Task LoginReportsOnboardingUnfinishedForANewTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await ConfirmAsync(userId);

        LoginResult result = await LoginAsync(owned);

        Assert.Equal(LoginOutcome.Succeeded, result.Outcome);
        Assert.False(result.OnboardingCompleted);
    }

    [SkippableFact]
    public async Task LoginReportsOnboardingFinishedOnceItIs()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await ConfirmAsync(userId);
        await Resolve<ITenantSetupService>(owned).CompleteAsync(tenantId);

        Assert.True((await LoginAsync(owned)).OnboardingCompleted);
    }

    // Verifying signs the user straight in, and lands them on the wizard.
    [SkippableFact]
    public async Task VerifyingTheCodeReportsOnboardingUnfinished()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await ConfirmAsync(userId);

        IssuedSession? session = await Resolve<ISessionIssuer>(owned).IssueAsync(userId, null);

        Assert.NotNull(session);
        Assert.False(session.OnboardingCompleted);
    }

    // A long-lived session must pick up completion that happened elsewhere.
    [SkippableFact]
    public async Task RefreshingPicksUpCompletionThatHappenedMidSession()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await ConfirmAsync(userId);

        LoginResult login = await LoginAsync(owned);
        Assert.False(login.OnboardingCompleted);

        await Resolve<ITenantSetupService>(owned).CompleteAsync(tenantId);

        RefreshResult refreshed = await Resolve<IRefreshTokenService>(owned)
            .RotateAsync(login.RefreshToken!.Value, null);

        Assert.True(refreshed.Succeeded);
        Assert.True(refreshed.OnboardingCompleted);
    }

    // Registration leaves the account unverified; login refuses until it is.
    private async Task ConfirmAsync(Guid userId)
    {
        await using MoteeDbContext context = fixture.CreateContext();

        ApplicationUser user = await context.Users
            .FirstAsync(candidate => candidate.Id == userId);

        user.EmailConfirmed = true;
        await context.SaveChangesAsync();
    }
}
