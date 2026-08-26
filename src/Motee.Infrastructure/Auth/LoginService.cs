using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Auth;
using Motee.Domain.Audit;
using Motee.Domain.Identity;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class LoginService(
    MoteeDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IAuditTrail audit,
    ISessionIssuer sessionIssuer) : ILoginService
{
    public async Task<LoginResult> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        string normalisedEmail = userManager.NormalizeEmail(request.Email.Trim());

        ApplicationUser? user = await dbContext.Users
            .FirstOrDefaultAsync(candidate => candidate.NormalizedEmail == normalisedEmail, cancellationToken);

        // Same answer for an unknown address as for a wrong password, so login
        // cannot be used to discover which addresses are registered.
        if (user is null)
        {
            return LoginResult.Failed(LoginOutcome.InvalidCredentials);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return LoginResult.Failed(LoginOutcome.LockedOut);
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            await userManager.AccessFailedAsync(user);

            // A wrong password against a real account is the entry an auditor looks for
            // first. The tenant is passed explicitly: no token exists yet, so nothing
            // has resolved which company this belongs to.
            audit.Record(
                AuditAction.Login,
                "admin.audit-trail",
                "Failed sign-in: incorrect password",
                entityId: user.Id,
                entityType: nameof(ApplicationUser),
                tenantId: user.TenantId,
                httpStatus: 401);

            await dbContext.SaveChangesAsync(cancellationToken);

            return LoginResult.Failed(
                await userManager.IsLockedOutAsync(user)
                    ? LoginOutcome.LockedOut
                    : LoginOutcome.InvalidCredentials);
        }

        // Checked after the password so an unverified account is not revealed to
        // someone who does not know the credentials.
        if (!user.EmailConfirmed)
        {
            return LoginResult.Failed(LoginOutcome.EmailNotConfirmed);
        }

        await userManager.ResetAccessFailedCountAsync(user);

        user.LastLoginAt = DateTimeOffset.UtcNow;

        audit.Record(
            AuditAction.Login,
            "admin.audit-trail",
            "Signed in",
            entityId: user.Id,
            entityType: nameof(ApplicationUser),
            tenantId: user.TenantId,
            httpStatus: 200);

        await dbContext.SaveChangesAsync(cancellationToken);

        IssuedSession? session =
            await sessionIssuer.IssueAsync(user.Id, request.IpAddress, cancellationToken);

        if (session is null)
        {
            return LoginResult.Failed(LoginOutcome.InvalidCredentials);
        }

        return new LoginResult
        {
            Outcome = LoginOutcome.Succeeded,
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            UserId = session.UserId,
            TenantId = session.TenantId,
            OnboardingCompleted = session.OnboardingCompleted,
        };
    }
}
