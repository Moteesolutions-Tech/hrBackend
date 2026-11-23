using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Motee.Application.Auth;
using Motee.Domain.Auth;
using Motee.Domain.Identity;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class RefreshTokenService(
    MoteeDbContext dbContext,
    IAccessTokenService accessTokens,
    Application.Common.IRequestContext requestContext,
    TimeProvider timeProvider,
    ILogger<RefreshTokenService> logger) : IRefreshTokenService
{
    private const int TokenBytes = 32;

    // Long enough for any real browser's string and short enough that a
    // deliberately enormous header cannot be used to fill the column.
    private const int AgentLength = 400;

    public async Task<IssuedRefreshToken> IssueAsync(
        Guid userId,
        string? createdByIp,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string raw = GenerateToken();

        DateTimeOffset expiresAt =
            RefreshTokenPolicy.ExpiresAt(now, await SessionMinutesAsync(userId, cancellationToken));

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            CreatedByIp = createdByIp,

            // Read from the request rather than passed in, so the two callers
            // cannot disagree about it. Null for anything issued without a
            // request, which is honest — the device list then shows it as
            // unknown rather than naming a browser nobody used.
            CreatedByUserAgent = Agent(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return new IssuedRefreshToken
        {
            Value = raw,
            ExpiresAt = expiresAt,
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

        // Re-read rather than carried over from the expiring token, so a
        // shortened session timeout takes effect on the next refresh instead of
        // waiting out whichever window was in force when the chain started.
        RefreshToken replacement = new()
        {
            Id = Guid.NewGuid(),
            UserId = token.UserId,
            TokenHash = Hash(replacementRaw),
            CreatedAt = now,
            ExpiresAt = RefreshTokenPolicy.ExpiresAt(
                now, await SessionMinutesAsync(token.UserId, cancellationToken)),
            CreatedByIp = usedByIp,
            CreatedByUserAgent = Agent(),
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
            PlatformRole = user.PlatformRole,
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

    // The company's session timeout, from the user's tenant.
    //
    // Read by user id rather than from the current tenant: a token is issued
    // during sign-in, before anything has resolved which company this is.
    // Null for platform staff, who belong to no company.
    private async Task<int?> SessionMinutesAsync(Guid userId, CancellationToken cancellationToken)
    {
        // The whole settings document, then read in memory. It is a jsonb column
        // behind a value converter, so projecting one of its members cannot be
        // translated to SQL.
        TenantSettings? settings = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.TenantId != null)
            .Join(
                dbContext.Tenants.AsNoTracking(),
                user => user.TenantId,
                tenant => tenant.Id,
                (_, tenant) => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        return settings?.PasswordPolicy.SessionTimeoutMinutes;
    }

    private string? Agent()
    {
        string? agent = requestContext.UserAgent;

        return string.IsNullOrWhiteSpace(agent)
            ? null
            : agent.Length <= AgentLength ? agent : agent[..AgentLength];
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
