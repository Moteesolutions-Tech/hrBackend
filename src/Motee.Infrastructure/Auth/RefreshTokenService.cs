using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Motee.Application.Auth;
using Motee.Domain.Auth;
using Motee.Domain.Identity;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class RefreshTokenService(
    MoteeDbContext dbContext,
    IAccessTokenService accessTokens,
    TimeProvider timeProvider,
    ILogger<RefreshTokenService> logger) : IRefreshTokenService
{
    private const int TokenBytes = 32;

    public async Task<IssuedRefreshToken> IssueAsync(
        Guid userId,
        string? createdByIp,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string raw = GenerateToken();

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = RefreshTokenPolicy.ExpiresAt(now),
            CreatedByIp = createdByIp,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return new IssuedRefreshToken
        {
            Value = raw,
            ExpiresAt = RefreshTokenPolicy.ExpiresAt(now),
        };
    }

    public async Task<RefreshResult> RotateAsync(
        string rawToken,
        string? usedByIp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return RefreshResult.Failed(RefreshOutcome.Revoked);
        }

        string hash = Hash(rawToken);

        RefreshToken? token = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken);

        // An unknown token is indistinguishable from a revoked one to the caller, so
        // probing tells an attacker nothing.
        if (token is null)
        {
            return RefreshResult.Failed(RefreshOutcome.Revoked);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        RefreshOutcome outcome = RefreshTokenPolicy.Evaluate(token, now);

        if (outcome == RefreshOutcome.Reused)
        {
            // Two parties hold this value. Which one is legitimate is unknowable, so
            // every live session for the user ends and both must sign in again.
            logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; revoking all sessions.",
                token.UserId);

            await RevokeAllAsync(token.UserId, cancellationToken);

            return RefreshResult.Failed(RefreshOutcome.Reused);
        }

        if (outcome != RefreshOutcome.Valid)
        {
            return RefreshResult.Failed(outcome);
        }

        ApplicationUser? user = await dbContext.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == token.UserId, cancellationToken);

        if (user is null)
        {
            return RefreshResult.Failed(RefreshOutcome.Revoked);
        }

        string replacementRaw = GenerateToken();

        RefreshToken replacement = new()
        {
            Id = Guid.NewGuid(),
            UserId = token.UserId,
            TokenHash = Hash(replacementRaw),
            CreatedAt = now,
            ExpiresAt = RefreshTokenPolicy.ExpiresAt(now),
            CreatedByIp = usedByIp,
        };

        dbContext.RefreshTokens.Add(replacement);

        token.RevokedAt = now;
        token.ReplacedByTokenId = replacement.Id;

        await dbContext.SaveChangesAsync(cancellationToken);

        DateTimeOffset? onboardingCompletedAt = await dbContext.Tenants
            .Where(tenant => tenant.Id == user.TenantId)
            .Select(tenant => tenant.OnboardingCompletedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // Re-read on every refresh rather than copied from the expiring token, so a
        // level withdrawn an hour ago is gone from the label too.
        List<string> levels = await dbContext.UserAccessLevels
            .Where(assignment => assignment.UserId == user.Id)
            .Join(
                dbContext.AccessLevels.Where(level =>
                    level.Status == Domain.Authorization.AccessLevelStatus.Active),
                assignment => assignment.AccessLevelId,
                level => level.Id,
                (_, level) => level.Name)
            .ToListAsync(cancellationToken);

        string role = user.IsOwner ? "owner" : string.Join(", ", levels);

        AccessToken accessToken = accessTokens.Issue(new TokenSubject
        {
            UserId = user.Id,
            Email = user.Email!,
            TenantId = user.TenantId,
            EmployeeId = user.EmployeeId,
            // Matches LoginService: an unassigned user falls back to the narrowest role.
            Role = role ?? Roles.ToSlug(Role.ReadOnly),
            IsPlatformStaff = user.IsPlatformStaff,
        });

        return new RefreshResult
        {
            Outcome = RefreshOutcome.Valid,
            AccessToken = accessToken,
            RefreshToken = new IssuedRefreshToken
            {
                Value = replacementRaw,
                ExpiresAt = replacement.ExpiresAt,
            },
            UserId = user.Id,
            TenantId = user.TenantId,
            OnboardingCompleted = onboardingCompletedAt is not null,
        };
    }

    public async Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        await dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAt, now),
                cancellationToken);
    }

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    // Hex SHA-256 — 64 characters, matching the column, and a fixed-length lookup key.
    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}
