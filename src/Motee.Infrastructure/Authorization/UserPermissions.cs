using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Motee.Application.Auth;
using Motee.Domain.Authorization;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Authorization;

internal sealed class UserPermissions(
    MoteeDbContext dbContext,
    IMemoryCache cache) : IUserPermissions
{
    // Short enough that a withdrawn level stops working within a minute even if the
    // eviction below is somehow missed, long enough that a burst of requests from one
    // page load costs a single query.
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    public async Task<ResolvedPermissions> ForAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(Key(userId), out ResolvedPermissions? cached) && cached is not null)
        {
            return cached;
        }

        ResolvedPermissions resolved = await LoadAsync(userId, cancellationToken);

        cache.Set(Key(userId), resolved, Lifetime);

        return resolved;
    }

    public void Forget(Guid userId) => cache.Remove(Key(userId));

    private async Task<ResolvedPermissions> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        bool isOwner = await dbContext.Users
            .Where(user => user.Id == userId)
            .Select(user => user.IsOwner)
            .FirstOrDefaultAsync(cancellationToken);

        // Only active levels grant. Draft is unfinished and inactive is withdrawn —
        // and withdrawing has to take effect at once, or deactivating a level is not
        // a control, only a label.
        //
        // Filters are ignored on purpose: this runs while resolving who the caller is,
        // which is before a tenant has been established. The user id is the constraint,
        // and a level always belongs to the same tenant as the user holding it.
        List<AccessLevel> held = await dbContext.UserAccessLevels
            .IgnoreQueryFilters()
            .Where(assignment => assignment.UserId == userId)
            .Join(
                dbContext.AccessLevels.IgnoreQueryFilters()
                    .Where(level => level.Status == AccessLevelStatus.Active),
                assignment => assignment.AccessLevelId,
                level => level.Id,
                (_, level) => level)
            .ToListAsync(cancellationToken);

        if (held.Count == 0)
        {
            // Not a denial: AccessDecision still applies the self-service floor, so
            // someone with nothing assigned reaches their own record.
            return new ResolvedPermissions
            {
                IsOwner = isOwner,
                Permissions = AccessLevelPermissions.Nothing,
                LevelNames = [],
            };
        }

        return new ResolvedPermissions
        {
            IsOwner = isOwner,
            Permissions = AccessLevelPermissions.Merge(held.Select(level =>
                new AccessLevelPermissions
                {
                    Modules = level.Permissions,
                    Scope = level.Scope,
                })),
            LevelNames = [.. held.Select(level => level.Name).Order(StringComparer.Ordinal)],
        };
    }

    private static string Key(Guid userId) => $"motee.permissions.{userId:N}";
}
