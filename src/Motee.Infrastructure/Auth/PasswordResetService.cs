using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Motee.Application.Auth;
using Motee.Domain.Auth;
using Motee.Domain.Settings;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class PasswordResetService(
    MoteeDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IOtpService otpService,
    IRefreshTokenService refreshTokens,
    TimeProvider timeProvider,
    ILogger<PasswordResetService> logger) : IPasswordResetService
{
    public async Task RequestAsync(string email, CancellationToken cancellationToken = default)
    {
        ApplicationUser? user = await FindAsync(email, cancellationToken);

        if (user is null)
        {
            // Returning quietly keeps "is this address registered?" unanswerable.
            logger.LogInformation("Password reset requested for an unknown address.");
            return;
        }

        await otpService.IssueAsync(user.Id, OtpPurpose.PasswordReset, cancellationToken);
    }

    public async Task<PasswordResetOutcome> ResetAsync(
        string email,
        string code,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ApplicationUser? user = await FindAsync(email, cancellationToken);

        // An unknown address is reported exactly as a wrong code would be.
        if (user is null)
        {
            return PasswordResetOutcome.InvalidCode;
        }

        OtpAttemptOutcome verification = await otpService.VerifyAsync(
            user.Id, OtpPurpose.PasswordReset, code, cancellationToken);

        if (verification != OtpAttemptOutcome.Verified)
        {
            return verification switch
            {
                OtpAttemptOutcome.Expired => PasswordResetOutcome.Expired,
                OtpAttemptOutcome.LockedOut => PasswordResetOutcome.LockedOut,
                OtpAttemptOutcome.AlreadyUsed => PasswordResetOutcome.AlreadyUsed,
                _ => PasswordResetOutcome.InvalidCode,
            };
        }

        // The verified OTP already proves control of the mailbox, so Identity's own
        // reset token would be a second proof of the same thing. The password is set
        // directly instead — validated first, because RemovePassword/AddPassword
        // would leave the account with no password at all if the new one were
        // rejected in between.
        foreach (IPasswordValidator<ApplicationUser> validator in userManager.PasswordValidators)
        {
            IdentityResult validation = await validator.ValidateAsync(userManager, user, newPassword);

            if (!validation.Succeeded)
            {
                // Reuse reported distinctly. The password may be perfectly
                // strong, and "choose a stronger one" is advice somebody cannot
                // act on when the real problem is that they have used it before.
                return validation.Errors.Any(error =>
                    error.Code == Settings.TenantPasswordValidator.PreviouslyUsedCode)
                    ? PasswordResetOutcome.PreviouslyUsed
                    : PasswordResetOutcome.WeakPassword;
            }
        }

        // The outgoing hash is kept before it is overwritten, where the
        // company's policy forbids reuse. Written here rather than by the
        // validator, which only ever reads: a validator that wrote would record
        // a password on every failed attempt at setting one.
        await RememberAsync(user, cancellationToken);

        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, newPassword);
        user.PasswordChangedAt = timeProvider.GetUtcNow();

        // Rotates the security stamp and persists both changes. Outstanding OTP codes
        // derive from that stamp, so any still in flight stop working.
        IdentityResult result = await userManager.UpdateSecurityStampAsync(user);

        if (!result.Succeeded)
        {
            return PasswordResetOutcome.WeakPassword;
        }

        // A reset is the response to a suspected compromise, so every existing
        // session ends — otherwise whoever prompted it keeps their access.
        await refreshTokens.RevokeAllAsync(user.Id, cancellationToken);

        // Resetting a password proves control of the mailbox, so an account still
        // waiting on verification becomes confirmed here.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);
        }

        return PasswordResetOutcome.Succeeded;
    }

    // Keeps the password being replaced, and trims the history to the depth the
    // company's policy asks for. With the policy off nothing is written and
    // anything previously written is removed: a table of everybody's old
    // password hashes that no rule reads is a liability with no purpose.
    private async Task RememberAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        PasswordPolicy policy = PasswordPolicy.Default;

        if (user.TenantId is Guid tenantId)
        {
            Domain.Tenants.TenantSettings? settings = await dbContext.Tenants
                .AsNoTracking()
                .Where(tenant => tenant.Id == tenantId)
                .Select(tenant => tenant.Settings)
                .FirstOrDefaultAsync(cancellationToken);

            policy = settings?.PasswordPolicy ?? PasswordPolicy.Default;
        }

        // One fewer row than the policy forbids. The password being set now is
        // the one in users.password_hash, and the validator checks that
        // separately — so a policy of five is this table's four plus the
        // current one. Keeping five here would forbid six.
        int rows = Math.Max(0, PasswordRules.HistoryDepth(policy) - 1);

        List<PasswordHistoryEntry> kept = await dbContext.PasswordHistory
            .Where(entry => entry.UserId == user.Id)
            .OrderByDescending(entry => entry.CreatedAt)
            .ToListAsync(cancellationToken);

        if (rows == 0)
        {
            // Either no reuse policy, or one that forbids only the password
            // being replaced. Neither needs a row, and anything previously
            // written goes: a table of old password hashes that no rule reads
            // is a liability with no purpose.
            dbContext.PasswordHistory.RemoveRange(kept);
            return;
        }

        if (user.PasswordHash is string outgoing)
        {
            dbContext.PasswordHistory.Add(new PasswordHistoryEntry
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PasswordHash = outgoing,
                CreatedAt = timeProvider.GetUtcNow(),
            });
        }

        // The entry just added takes the newest place, so one fewer of the old
        // ones survives.
        dbContext.PasswordHistory.RemoveRange(kept.Skip(rows - 1));
    }

    private Task<ApplicationUser?> FindAsync(string email, CancellationToken cancellationToken)
    {
        string normalised = userManager.NormalizeEmail(email.Trim());

        return dbContext.Users
            .FirstOrDefaultAsync(user => user.NormalizedEmail == normalised, cancellationToken);
    }
}
