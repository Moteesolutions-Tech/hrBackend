using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class LoginServiceTests(PostgresFixture fixture)
{
    private const string Password = "correct-horse";

    private static RegisterTenantRequest Request(string company, string email) => new()
    {
        FirstName = "Ada",
        LastName = "Okafor",
        Email = email,
        CompanyName = company,
        Password = Password,
        CountryCode = "NG",
    };

    // Registration leaves the email unconfirmed; most login paths need it verified.
    private async Task<Guid> RegisterConfirmedAsync(string company, string email)
    {
        RegisterTenantResult registration = await fixture.RegisterAsync(Request(company, email));
        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        await using MoteeDbContext context = fixture.CreateContext();
        ApplicationUser user = await context.Users.SingleAsync(candidate => candidate.Id == registration.UserId);
        user.EmailConfirmed = true;
        await context.SaveChangesAsync();

        return registration.UserId;
    }

    private async Task<LoginResult> LoginAsync(string email, string password)
    {
        await using ServiceProvider provider = fixture.BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ILoginService>()
            .LoginAsync(new LoginRequest { Email = email, Password = password });
    }

    [SkippableFact]
    public async Task IssuesATokenForValidCredentials()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        Guid userId = await RegisterConfirmedAsync("Acme Corporation", "ada@acme.com");

        LoginResult result = await LoginAsync("ada@acme.com", Password);

        Assert.Equal(LoginOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.AccessToken);
        Assert.Equal(userId, result.UserId);
        Assert.NotNull(result.TenantId);
    }

    [SkippableFact]
    public async Task MatchesTheEmailWithoutRegardToCase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();
        await RegisterConfirmedAsync("Acme Corporation", "ada@acme.com");

        Assert.Equal(LoginOutcome.Succeeded, (await LoginAsync("ADA@Acme.COM", Password)).Outcome);
    }

    [SkippableFact]
    public async Task RejectsAWrongPassword()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();
        await RegisterConfirmedAsync("Acme Corporation", "ada@acme.com");

        LoginResult result = await LoginAsync("ada@acme.com", "wrong-password");

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.AccessToken);
    }

    // An unknown address must be indistinguishable from a wrong password, or login
    // becomes a way to enumerate who has an account.
    [SkippableFact]
    public async Task GivesTheSameAnswerForAnUnknownAddress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();
        await RegisterConfirmedAsync("Acme Corporation", "ada@acme.com");

        LoginResult unknown = await LoginAsync("nobody@nowhere.com", Password);
        LoginResult wrongPassword = await LoginAsync("ada@acme.com", "wrong-password");

        Assert.Equal(wrongPassword.Outcome, unknown.Outcome);
    }

    [SkippableFact]
    public async Task RefusesAnUnverifiedAccount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        await fixture.RegisterAsync(Request("Acme Corporation", "ada@acme.com"));

        LoginResult result = await LoginAsync("ada@acme.com", Password);

        Assert.Equal(LoginOutcome.EmailNotConfirmed, result.Outcome);
        Assert.Null(result.AccessToken);
    }

    // Whether the account is verified must not leak to someone without the password.
    [SkippableFact]
    public async Task DoesNotRevealVerificationStateOnAWrongPassword()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        await fixture.RegisterAsync(Request("Acme Corporation", "ada@acme.com"));

        LoginResult result = await LoginAsync("ada@acme.com", "wrong-password");

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
    }

    [SkippableFact]
    public async Task LocksOutAfterRepeatedFailures()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();
        await RegisterConfirmedAsync("Acme Corporation", "ada@acme.com");

        for (int attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync("ada@acme.com", "wrong-password");
        }

        LoginResult result = await LoginAsync("ada@acme.com", Password);

        Assert.Equal(LoginOutcome.LockedOut, result.Outcome);
    }

}
