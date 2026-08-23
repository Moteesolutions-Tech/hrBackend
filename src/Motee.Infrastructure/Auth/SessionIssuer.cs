using Microsoft.EntityFrameworkCore;
using Motee.Application.Auth;
using Motee.Domain.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class SessionIssuer(
    MoteeDbContext dbContext,
    IAccessTokenService accessTokens,
    IRefreshTokenService refreshTokens) : ISessionIssuer
{
    public async Task<IssuedSession?> IssueAsync(
        Guid userId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        // User, role and the tenant's onboarding state in one round trip.
        SessionRow? row = await dbContext.Users
            .Where(user => user.Id == userId)
            .Select(user => new SessionRow
            {
                UserId = user.Id,
                Email = user.Email!,
                TenantId = user.TenantId,
                EmployeeId = user.EmployeeId,
                IsPlatformStaff = user.IsPlatformStaff,
                IsOwner = user.IsOwner,

                // The levels they hold, for display. Authorization resolves these
                // again per request, so what is written here can never grant anything
                // — it only labels the session.
                Levels = dbContext.UserAccessLevels
                    .Where(assignment => assignment.UserId == user.Id)
                    .Join(
                        dbContext.AccessLevels.Where(level =>
                            level.Status == Domain.Authorization.AccessLevelStatus.Active),
                        assignment => assignment.AccessLevelId,
                        level => level.Id,
                        (_, level) => level.Name)
                    .ToList(),
                OnboardingCompletedAt = dbContext.Tenants
                    .Where(tenant => tenant.Id == user.TenantId)
                    .Select(tenant => tenant.OnboardingCompletedAt)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        AccessToken accessToken = accessTokens.Issue(new TokenSubject
        {
            UserId = row.UserId,
            Email = row.Email,
            TenantId = row.TenantId,
            EmployeeId = row.EmployeeId,
            // Display only. Someone holding nothing gets an empty string rather than
            // a fallback role — they are not "read-only", they simply hold no level,
            // and the self-service floor is what reaches their own record.
            Role = row.IsOwner ? "owner" : string.Join(", ", row.Levels),
            IsPlatformStaff = row.IsPlatformStaff,
        });

        IssuedRefreshToken refreshToken =
            await refreshTokens.IssueAsync(row.UserId, ipAddress, cancellationToken);

        return new IssuedSession
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            UserId = row.UserId,
            TenantId = row.TenantId,
            OnboardingCompleted = row.OnboardingCompletedAt is not null,
        };
    }

    private sealed class SessionRow
    {
        public required Guid UserId { get; init; }

        public required string Email { get; init; }

        public Guid? TenantId { get; init; }

        public Guid? EmployeeId { get; init; }

        public required bool IsPlatformStaff { get; init; }

        public required bool IsOwner { get; init; }

        public required IReadOnlyList<string> Levels { get; init; }

        public DateTimeOffset? OnboardingCompletedAt { get; init; }
    }
}
