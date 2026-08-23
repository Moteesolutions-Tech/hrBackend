using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Domain.Auth;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class PasswordResetServiceTests(PostgresFixture fixture)
{
    private const string Email = "ada@acme.com";
    private const string OldPassword = "correct-horse";
    private const string NewPassword = "battery-staple";

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public string LastCode => Regex.Match(Sent[^1].Body, @"\b\d{6}\b").Value;
    }

    private async Task<(Guid UserId, RecordingEmailSender Mail, ServiceProvider Provider)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = Email,
            CompanyName = "Acme Corporation",
            Password = OldPassword,
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        RecordingEmailSender mail = new();
        ServiceProvider provider = fixture.BuildProvider(services =>
            services.AddScoped<IEmailSender>(_ => mail));

        return (registration.UserId, mail, provider);
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    [SkippableFact]
    public async Task SendsACodeAndAcceptsTheNewPassword()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Resolve<IPasswordResetService>(owned).RequestAsync(Email);

        Assert.Single(mail.Sent);

        PasswordResetOutcome outcome = await Resolve<IPasswordResetService>(owned)
            .ResetAsync(Email, mail.LastCode, NewPassword);

        Assert.Equal(PasswordResetOutcome.Succeeded, outcome);
    }

    [SkippableFact]
    public async Task TheNewPasswordWorksAndTheOldOneDoesNot()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Resolve<IPasswordResetService>(owned).RequestAsync(Email);
        await Resolve<IPasswordResetService>(owned).ResetAsync(Email, mail.LastCode, NewPassword);

        LoginResult withNew = await Resolve<ILoginService>(owned)
            .LoginAsync(new LoginRequest { Email = Email, Password = NewPassword });
        LoginResult withOld = await Resolve<ILoginService>(owned)
            .LoginAsync(new LoginRequest { Email = Email, Password = OldPassword });

        Assert.Equal(LoginOutcome.Succeeded, withNew.Outcome);
        Assert.NotEqual(LoginOutcome.Succeeded, withOld.Outcome);
    }

    // A reset answers a suspected compromise. Leaving existing sessions alive would
    // keep whoever prompted it signed in.
    [SkippableFact]
    public async Task EveryExistingSessionEnds()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        IssuedRefreshToken session = await Resolve<IRefreshTokenService>(owned)
            .IssueAsync(userId, null);

        await Resolve<IPasswordResetService>(owned).RequestAsync(Email);
        await Resolve<IPasswordResetService>(owned).ResetAsync(Email, mail.LastCode, NewPassword);

        RefreshResult afterReset = await Resolve<IRefreshTokenService>(owned)
            .RotateAsync(session.Value, null);

        Assert.False(afterReset.Succeeded);
    }

    // An email-verification code must not double as a password-reset code.
    [SkippableFact]
    public async Task AVerificationCodeCannotResetThePassword()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Resolve<IOtpService>(owned).IssueAsync(userId, OtpPurpose.EmailVerification);
        string verificationCode = mail.LastCode;

        PasswordResetOutcome outcome = await Resolve<IPasswordResetService>(owned)
            .ResetAsync(Email, verificationCode, NewPassword);

        Assert.NotEqual(PasswordResetOutcome.Succeeded, outcome);
    }

    [SkippableFact]
    public async Task AWrongCodeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Resolve<IPasswordResetService>(owned).RequestAsync(Email);

        Assert.Equal(
            PasswordResetOutcome.InvalidCode,
            await Resolve<IPasswordResetService>(owned).ResetAsync(Email, "000000", NewPassword));
    }

    [SkippableFact]
    public async Task ACodeCannotBeUsedTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Resolve<IPasswordResetService>(owned).RequestAsync(Email);
        string code = mail.LastCode;

        await Resolve<IPasswordResetService>(owned).ResetAsync(Email, code, NewPassword);

        Assert.Equal(
            PasswordResetOutcome.AlreadyUsed,
            await Resolve<IPasswordResetService>(owned).ResetAsync(Email, code, "third-password"));
    }

    // Requesting a reset for an address that does not exist must be indistinguishable
    // from one that does — no mail, no error, no timing tell.
    [SkippableFact]
    public async Task AnUnknownAddressIsSilentlyIgnored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Resolve<IPasswordResetService>(owned).RequestAsync("nobody@nowhere.com");

        Assert.Empty(mail.Sent);
    }

    [SkippableFact]
    public async Task AnUnknownAddressLooksExactlyLikeAWrongCode()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.Equal(
            PasswordResetOutcome.InvalidCode,
            await Resolve<IPasswordResetService>(owned)
                .ResetAsync("nobody@nowhere.com", "123456", NewPassword));
    }

    [SkippableFact]
    public async Task MatchesTheAddressWithoutRegardToCase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Resolve<IPasswordResetService>(owned).RequestAsync("ADA@ACME.COM");

        Assert.Single(mail.Sent);

        Assert.Equal(
            PasswordResetOutcome.Succeeded,
            await Resolve<IPasswordResetService>(owned)
                .ResetAsync("Ada@Acme.Com", mail.LastCode, NewPassword));
    }

    [SkippableFact]
    public async Task ResettingConfirmsAnUnverifiedAccount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Resolve<IPasswordResetService>(owned).RequestAsync(Email);
        await Resolve<IPasswordResetService>(owned).ResetAsync(Email, mail.LastCode, NewPassword);

        await using MoteeDbContext context = fixture.CreateContext();

        Assert.True(await context.Users
            .Where(user => user.Id == userId)
            .Select(user => user.EmailConfirmed)
            .FirstAsync());
    }
}
