using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Notifications;
using Motee.Domain.Auth;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class OtpServiceTests(PostgresFixture fixture)
{
    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public string LastCode => Regex.Match(Sent[^1].TextBody, @"\b\d{6}\b").Value;
    }

    private static RegisterTenantRequest Request(string email = "ada@acme.com") => new()
    {
        FirstName = "Ada",
        LastName = "Okafor",
        Email = email,
        CompanyName = "Acme Corporation",
        Password = "correct-horse",
        CountryCode = "NG",
    };

    private async Task<(Guid UserId, RecordingEmailSender Mail, ServiceProvider Provider)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(Request());
        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        RecordingEmailSender mail = new();
        ServiceProvider provider = fixture.BuildProvider(services =>
            services.AddScoped<IEmailSender>(_ => mail));

        return (registration.UserId, mail, provider);
    }

    private static IOtpService Otp(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IOtpService>();

    [SkippableFact]
    public async Task IssuingSendsASixDigitCodeAndRecordsTheChallenge()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        OtpIssueResult result = await Otp(provider)
            .IssueAsync(userId, OtpPurpose.EmailVerification);

        Assert.True(result.Sent);
        Assert.Single(mail.Sent);
        Assert.Equal("ada@acme.com", mail.Sent[0].To);
        Assert.Matches(@"\b\d{6}\b", mail.Sent[0].TextBody);

        await using MoteeDbContext context = fixture.CreateContext();
        OtpChallengeRecord challenge = await context.OtpChallenges
            .SingleAsync(record => record.UserId == userId);

        Assert.Equal(OtpPurpose.EmailVerification, challenge.Purpose);
        Assert.Equal(0, challenge.FailedAttempts);
        Assert.Null(challenge.ConsumedAt);
    }

    [SkippableFact]
    public async Task ResendingDuringTheCooldownIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Otp(provider).IssueAsync(userId, OtpPurpose.EmailVerification);
        OtpIssueResult second = await Otp(provider).IssueAsync(userId, OtpPurpose.EmailVerification);

        Assert.False(second.Sent);
        Assert.True(second.RetryAfter > TimeSpan.Zero);
        Assert.Single(mail.Sent);
    }

    [SkippableFact]
    public async Task VerifyingTheCorrectCodeConfirmsTheEmail()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Otp(provider).IssueAsync(userId, OtpPurpose.EmailVerification);

        OtpAttemptOutcome outcome = await Otp(provider)
            .VerifyAsync(userId, OtpPurpose.EmailVerification, mail.LastCode);

        Assert.Equal(OtpAttemptOutcome.Verified, outcome);

        await using MoteeDbContext context = fixture.CreateContext();
        ApplicationUser user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
        OtpChallengeRecord challenge = await context.OtpChallenges
            .SingleAsync(record => record.UserId == userId);

        Assert.True(user.EmailConfirmed);
        Assert.NotNull(challenge.ConsumedAt);
    }

    [SkippableFact]
    public async Task AWrongCodeIsCountedAgainstTheAttemptBudget()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Otp(provider).IssueAsync(userId, OtpPurpose.EmailVerification);

        OtpAttemptOutcome outcome = await Otp(provider)
            .VerifyAsync(userId, OtpPurpose.EmailVerification, "000000");

        Assert.Equal(OtpAttemptOutcome.IncorrectCode, outcome);

        await using MoteeDbContext context = fixture.CreateContext();
        OtpChallengeRecord challenge = await context.OtpChallenges
            .SingleAsync(record => record.UserId == userId);

        Assert.Equal(1, challenge.FailedAttempts);
        Assert.False((await context.Users.SingleAsync(user => user.Id == userId)).EmailConfirmed);
    }

    [SkippableFact]
    public async Task ExhaustingAttemptsLocksTheChallengeEvenForTheRightCode()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Otp(provider).IssueAsync(userId, OtpPurpose.EmailVerification);

        for (int attempt = 0; attempt < OtpPolicy.MaxAttempts; attempt++)
        {
            await Otp(provider).VerifyAsync(userId, OtpPurpose.EmailVerification, "000000");
        }

        OtpAttemptOutcome outcome = await Otp(provider)
            .VerifyAsync(userId, OtpPurpose.EmailVerification, mail.LastCode);

        Assert.Equal(OtpAttemptOutcome.LockedOut, outcome);
    }

    [SkippableFact]
    public async Task ACodeCannotBeUsedTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Otp(provider).IssueAsync(userId, OtpPurpose.EmailVerification);
        await Otp(provider).VerifyAsync(userId, OtpPurpose.EmailVerification, mail.LastCode);

        OtpAttemptOutcome replay = await Otp(provider)
            .VerifyAsync(userId, OtpPurpose.EmailVerification, mail.LastCode);

        Assert.Equal(OtpAttemptOutcome.AlreadyUsed, replay);
    }

    // A password-reset code must not confirm an email address.
    [SkippableFact]
    public async Task ACodeIssuedForOnePurposeDoesNotWorkForAnother()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, RecordingEmailSender mail, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Otp(provider).IssueAsync(userId, OtpPurpose.PasswordReset);
        string resetCode = mail.LastCode;

        await Otp(provider).IssueAsync(userId, OtpPurpose.EmailVerification);

        OtpAttemptOutcome outcome = await Otp(provider)
            .VerifyAsync(userId, OtpPurpose.EmailVerification, resetCode);

        Assert.Equal(OtpAttemptOutcome.IncorrectCode, outcome);
    }

    [SkippableFact]
    public async Task VerifyingWithNothingIssuedLooksLikeExpiry()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        OtpAttemptOutcome outcome = await Otp(provider)
            .VerifyAsync(userId, OtpPurpose.EmailVerification, "123456");

        Assert.Equal(OtpAttemptOutcome.Expired, outcome);
    }
}
