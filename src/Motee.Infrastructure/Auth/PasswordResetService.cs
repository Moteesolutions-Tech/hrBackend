using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Motee.Application.Auth;
using Motee.Domain.Auth;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class PasswordResetService(
    MoteeDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IOtpService otpService,
    IRefreshTokenService refreshTokens,
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
                return PasswordResetOutcome.WeakPassword;
            }
        }

        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, newPassword);

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

    private Task<ApplicationUser?> FindAsync(string email, CancellationToken cancellationToken)
    {
        string normalised = userManager.NormalizeEmail(email.Trim());

        return dbContext.Users
            .FirstOrDefaultAsync(user => user.NormalizedEmail == normalised, cancellationToken);
    }
}
