using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Notifications;
using Motee.Domain.Auth;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class OtpService(
    MoteeDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IEmailDispatcher email,
    IDebugMode debugMode,
    ILogger<OtpService> logger,
    TimeProvider timeProvider) : IOtpService
{
    public async Task<OtpIssueResult> IssueAsync(
        Guid userId,
        OtpPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ApplicationUser user = await RequireUserAsync(userId, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        OtpChallengeRecord? record = await FindAsync(userId, purpose, cancellationToken);

        if (record is not null && !record.ToChallenge().ConsumedAt.HasValue)
        {
            if (!OtpPolicy.CanResend(record.ToChallenge(), now))
            {
                return new OtpIssueResult
                {
                    Sent = false,
                    RetryAfter = OtpPolicy.RetryAfter(record.ToChallenge(), now),
                };
            }
        }

        string code = debugMode.Enabled
            ? DebugMode.FixedOtpCode
            : await userManager.GenerateUserTokenAsync(
                user, TokenOptions.DefaultEmailProvider, TokenPurpose(purpose));

        if (record is null)
        {
            record = new OtpChallengeRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Purpose = purpose,
                IssuedAt = now,
                LastSentAt = now,
            };

            dbContext.OtpChallenges.Add(record);
        }
        else
        {
            // A resend restarts the window and the attempt budget.
            record.IssuedAt = now;
            record.LastSentAt = now;
            record.FailedAttempts = 0;
            record.ConsumedAt = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (debugMode.Enabled)
        {
            // Nothing to deliver: the code is fixed and known.
            logger.LogWarning(
                "Debug OTP mode active — {Purpose} for {Email} accepts the fixed code.",
                purpose,
                user.Email);

            return new OtpIssueResult { Sent = true };
        }

        email.Send(user.Email!, new OtpCodeEmail
        {
            Code = code,
            Lifetime = OtpPolicy.Lifetime,
            Purpose = purpose,
        });

        return new OtpIssueResult { Sent = true };
    }

    public async Task<OtpAttemptOutcome> VerifyAsync(
        Guid userId,
        OtpPurpose purpose,
        string code,
        CancellationToken cancellationToken = default)
    {
        ApplicationUser user = await RequireUserAsync(userId, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        OtpChallengeRecord? record = await FindAsync(userId, purpose, cancellationToken);

        // Nothing issued is indistinguishable from expired, so an attacker learns
        // nothing about whether a code was ever sent.
        if (record is null)
        {
            return OtpAttemptOutcome.Expired;
        }

        // Only the comparison changes in debug mode; expiry, the attempt budget and
        // single-use all still apply, so the flow behaves as it will in production.
        bool codeMatched = debugMode.Enabled
            ? string.Equals(code, DebugMode.FixedOtpCode, StringComparison.Ordinal)
            : await userManager.VerifyUserTokenAsync(
                user, TokenOptions.DefaultEmailProvider, TokenPurpose(purpose), code);

        OtpAttemptOutcome outcome = OtpPolicy.Evaluate(record.ToChallenge(), codeMatched, now);

        switch (outcome)
        {
            case OtpAttemptOutcome.Verified:
                record.Apply(OtpPolicy.Consume(record.ToChallenge(), now));

                if (purpose == OtpPurpose.EmailVerification)
                {
                    user.EmailConfirmed = true;
                    await userManager.UpdateAsync(user);
                }

                break;

            case OtpAttemptOutcome.IncorrectCode:
                record.Apply(OtpPolicy.RecordFailure(record.ToChallenge()));
                break;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return outcome;
    }

    private Task<OtpChallengeRecord?> FindAsync(
        Guid userId,
        OtpPurpose purpose,
        CancellationToken cancellationToken) =>
        dbContext.OtpChallenges.FirstOrDefaultAsync(
            challenge => challenge.UserId == userId && challenge.Purpose == purpose,
            cancellationToken);

    private async Task<ApplicationUser> RequireUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await dbContext.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        return user ?? throw new InvalidOperationException($"User {userId} not found.");
    }

    // Distinct purposes so a password-reset code cannot verify an email.
    private static string TokenPurpose(OtpPurpose purpose) => purpose switch
    {
        OtpPurpose.EmailVerification => "motee:email-verification",
        OtpPurpose.PasswordReset => "motee:password-reset",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

}
