using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Auth;
using Motee.Domain.Audit;
using Motee.Domain.Identity;
using Motee.Domain.Settings;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class LoginService(
    MoteeDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IAuditTrail audit,
    ISessionIssuer sessionIssuer,
    TimeProvider timeProvider) : ILoginService
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

        // The company's expiry policy. Checked here, with the right password in
        // hand, because that is the only moment it can be: there is no other
        // point at which somebody presents a password to be judged.
        //
        // The way back is the forgot-password flow, which this product has — so
        // this is a door somebody can walk through rather than a lockout. An
        // account with no recorded change date never expires, which matters on
        // the day a company first saves a policy: nobody has one, and expiring
        // everybody at once is not what they asked for.
        if (await HasLapsedAsync(user, cancellationToken))
        {
            audit.Record(
                AuditAction.Login,
                "admin.audit-trail",
                "Sign-in refused: password has expired under the company policy",
                entityId: user.Id,
                entityType: nameof(ApplicationUser),
                tenantId: user.TenantId,
                httpStatus: 401);

            await dbContext.SaveChangesAsync(cancellationToken);

            return LoginResult.Failed(LoginOutcome.PasswordExpired);
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

    private async Task<bool> HasLapsedAsync(
        ApplicationUser user,
        CancellationToken cancellationToken)
    {
        if (user.TenantId is not Guid tenantId)
        {
            // Platform staff. They belong to no company, so no company's policy
            // applies to them.
            return false;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        PasswordPolicy policy = settings?.PasswordPolicy ?? PasswordPolicy.Default;

        return PasswordRules.HasExpired(
            policy, user.PasswordChangedAt, timeProvider.GetUtcNow());
    }
}
