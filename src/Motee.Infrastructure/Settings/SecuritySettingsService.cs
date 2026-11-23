using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Common;
using Motee.Application.Settings;
using Motee.Domain.Audit;
using Motee.Domain.Auth;
using Motee.Domain.Settings;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Settings;

// The security screen's two lists.
//
// Neither needed a table. A trusted-devices table would be a list that revokes
// nothing — the device would carry on using its refresh token while the screen
// showed it gone — so the list is the live refresh tokens, and revoking one
// signs that device out for real. Login activity comes from audit_entries,
// which already records both a sign-in and a failed attempt with the IP, the
// agent and the moment.
internal sealed class SecuritySettingsService(
    MoteeDbContext dbContext,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider timeProvider) : ISecuritySettingsService
{
    // The screen shows a handful. Enough that somebody can find the sign-in
    // they do not recognise, few enough that the list is readable.
    private const int MaximumActivity = 100;

    public async Task<IReadOnlyList<DeviceSessionDto>> SessionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (UserId() is not Guid userId)
        {
            return [];
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        List<RefreshToken> live = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(token => token.UserId == userId
                && token.RevokedAt == null
                && token.ReplacedByTokenId == null
                && token.ExpiresAt > now)
            .OrderByDescending(token => token.CreatedAt)
            .ToListAsync(cancellationToken);

        // Which row is this device. The request's own agent and IP, because the
        // access token carries no reference to the refresh token it came from —
        // rotating one would otherwise mark nothing as current and the screen
        // would offer to revoke the session you are reading it on.
        RefreshToken? current = live.FirstOrDefault(token =>
            string.Equals(token.CreatedByUserAgent, requestContext.UserAgent, StringComparison.Ordinal)
            && string.Equals(token.CreatedByIp, requestContext.IpAddress, StringComparison.Ordinal));

        return
        [
            .. live.Select(token =>
            {
                DeviceName device = UserAgents.Describe(token.CreatedByUserAgent);

                return new DeviceSessionDto
                {
                    Id = token.Id,
                    Name = device.Label,
                    Browser = device.Browser,
                    OperatingSystem = device.OperatingSystem,
                    IpAddress = token.CreatedByIp,
                    SignedInAt = token.CreatedAt,
                    ExpiresAt = token.ExpiresAt,
                    IsCurrent = current is not null && token.Id == current.Id,
                };
            }),
        ];
    }

    public async Task<SettingsOutcome> RevokeSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (UserId() is not Guid userId)
        {
            return SettingsOutcome.NotFound;
        }

        // Scoped to the caller, so a valid id is not permission to sign
        // somebody else's device out.
        RefreshToken? token = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(row => row.Id == id && row.UserId == userId, cancellationToken);

        if (token is null)
        {
            return SettingsOutcome.NotFound;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Already gone counts as done. The screen's list can be a few seconds
        // stale, and reporting a failure for a device that is already signed
        // out is a failure about nothing.
        if (token.RevokedAt is null)
        {
            token.RevokedAt = now;

            audit.Record(
                AuditAction.Update,
                "admin.settings",
                $"Signed out {UserAgents.Describe(token.CreatedByUserAgent).Label}",
                entityId: token.Id,
                entityType: nameof(RefreshToken));

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return SettingsOutcome.Succeeded;
    }

    public async Task<IReadOnlyList<AccountEventDto>> ActivityAsync(
        int take = 25,
        CancellationToken cancellationToken = default)
    {
        if (UserId() is not Guid userId)
        {
            return [];
        }

        int wanted = Math.Clamp(take, 1, MaximumActivity);

        // The audit trail records a sign-in against the user it was for, as the
        // entity, which is how a failed attempt is attributable at all: nobody
        // is signed in yet, so there is no actor to record.
        //
        // Filters ignored because a failed sign-in is written before a tenant
        // has been resolved, so the entry carries the user's tenant while the
        // request carries none.
        List<AuditEntry> entries = await dbContext.AuditEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry => entry.EntityId == userId
                && (entry.ActorUserId == userId || entry.ActorUserId == null))
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(wanted)
            .ToListAsync(cancellationToken);

        return
        [
            .. entries.Select(entry =>
            {
                AccountEventKind kind = AccountEvents.KindOf(
                    entry.Description, entry.HttpStatus, entry.Action.ToString());

                return new AccountEventDto
                {
                    Id = entry.Id,
                    Kind = kind,
                    Event = entry.Description,
                    IpAddress = entry.IpAddress,
                    Device = UserAgents.Describe(entry.UserAgent).Label,
                    Succeeded = AccountEvents.Succeeded(kind),
                    At = entry.CreatedAt,
                };
            }),
        ];
    }

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;
}
