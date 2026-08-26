using Microsoft.EntityFrameworkCore;
using Motee.Application.Auth;
using Motee.Application.Authorization;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Authorization;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Authorization;

internal sealed class AccessLevelService(
    MoteeDbContext dbContext,
    IUserPermissions userPermissions,
    IRequestContext requestContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IAccessLevelService
{
    public async Task<IReadOnlyList<AccessLevelDto>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await Project(dbContext.AccessLevels.AsNoTracking())
            .OrderBy(level => level.Name)
            .ToListAsync(cancellationToken);

    public Task<AccessLevelDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Project(dbContext.AccessLevels.AsNoTracking().Where(level => level.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<AccessLevelResult> CreateAsync(
        AccessLevelRequest request,
        CancellationToken cancellationToken = default)
    {
        AccessLevelOutcome? problem = await ValidateAsync(request, null, cancellationToken);

        if (problem is not null)
        {
            return AccessLevelResult.Failed(problem.Value);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Copying starts from what the source grants, which the request then overrides.
        // Anything the caller sent wins, so a copy is a starting point rather than a
        // second source of truth.
        AccessLevel? source = request.CopyFromId is Guid copyFrom
            ? await dbContext.AccessLevels
                .FirstOrDefaultAsync(level => level.Id == copyFrom, cancellationToken)
            : null;

        AccessLevel level = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = Trimmed(request.Description) ?? source?.Description,
            Kind = AccessLevelKind.Custom,

            // Draft, not active. A level built in five clicks and live immediately is
            // how someone is granted payroll access by accident.
            Status = AccessLevelStatus.Draft,
            Scope = request.Scope,
            Permissions = Expand(request.Permissions),
            CreatedAt = now,
            CreatedByUserId = CurrentUser(),
            UpdatedAt = now,
            UpdatedByUserId = CurrentUser(),
        };

        dbContext.AccessLevels.Add(level);
        await dbContext.SaveChangesAsync(cancellationToken);

        return AccessLevelResult.Ok((await GetAsync(level.Id, cancellationToken))!);
    }

    public async Task<AccessLevelResult> UpdateAsync(
        Guid id,
        AccessLevelRequest request,
        CancellationToken cancellationToken = default)
    {
        AccessLevel? level = await dbContext.AccessLevels
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (level is null)
        {
            return AccessLevelResult.Failed(AccessLevelOutcome.NotFound);
        }

        AccessLevelOutcome? problem = await ValidateAsync(request, id, cancellationToken);

        if (problem is not null)
        {
            return AccessLevelResult.Failed(problem.Value);
        }

        level.Name = request.Name.Trim();
        level.Description = Trimmed(request.Description);
        level.Scope = request.Scope;
        level.Permissions = Expand(request.Permissions);
        level.UpdatedAt = timeProvider.GetUtcNow();
        level.UpdatedByUserId = CurrentUser();

        await dbContext.SaveChangesAsync(cancellationToken);

        // Everyone holding it is now on different permissions. Waiting for the cache
        // to lapse would leave a withdrawn grant working for another minute.
        await ForgetHoldersAsync(id, cancellationToken);

        return AccessLevelResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<AccessLevelResult> ChangeStatusAsync(
        Guid id,
        AccessLevelStatus status,
        CancellationToken cancellationToken = default)
    {
        AccessLevel? level = await dbContext.AccessLevels
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (level is null)
        {
            return AccessLevelResult.Failed(AccessLevelOutcome.NotFound);
        }

        level.Status = status;
        level.UpdatedAt = timeProvider.GetUtcNow();
        level.UpdatedByUserId = CurrentUser();

        await dbContext.SaveChangesAsync(cancellationToken);

        // Deactivating has to bite at once, or it is a label rather than a control.
        await ForgetHoldersAsync(id, cancellationToken);

        return AccessLevelResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<AccessLevelResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        AccessLevel? level = await dbContext.AccessLevels
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (level is null)
        {
            return AccessLevelResult.Failed(AccessLevelOutcome.NotFound);
        }

        bool held = await dbContext.UserAccessLevels
            .AnyAsync(assignment => assignment.AccessLevelId == id, cancellationToken);

        if (held)
        {
            return AccessLevelResult.Failed(AccessLevelOutcome.InUse);
        }

        dbContext.AccessLevels.Remove(level);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AccessLevelResult { Outcome = AccessLevelOutcome.Succeeded };
    }

    public async Task<AccessLevelResult> AssignAsync(
        Guid userId,
        Guid accessLevelId,
        CancellationToken cancellationToken = default)
    {
        AccessLevel? level = await dbContext.AccessLevels
            .FirstOrDefaultAsync(candidate => candidate.Id == accessLevelId, cancellationToken);

        if (level is null)
        {
            return AccessLevelResult.Failed(AccessLevelOutcome.NotFound);
        }

        // Draft is unfinished, inactive is withdrawn. Handing either to someone would
        // grant nothing while looking like it had worked.
        if (level.Status != AccessLevelStatus.Active)
        {
            return AccessLevelResult.Failed(AccessLevelOutcome.NotAssignable);
        }

        // The tenant clause is load-bearing. Users are deliberately not ITenantScoped —
        // login must find someone by email before any tenant is known — so nothing
        // filters this query but this line. Without it, an admin who knows a user id
        // from another company can hand that person one of their own access levels,
        // changing what someone in a company they do not administer is allowed to do.
        //
        // A user id is a GUID and not enumerable, which bounds how reachable that is;
        // it does not make the check optional.
        bool userInThisTenant = await dbContext.Users.AnyAsync(
            user => user.Id == userId
                && user.TenantId == currentTenant.TenantId
                && !user.IsPlatformStaff,
            cancellationToken);

        if (!userInThisTenant)
        {
            return AccessLevelResult.Failed(AccessLevelOutcome.UserNotFound);
        }

        bool alreadyHeld = await dbContext.UserAccessLevels.AnyAsync(
            assignment => assignment.UserId == userId && assignment.AccessLevelId == accessLevelId,
            cancellationToken);

        if (alreadyHeld)
        {
            return AccessLevelResult.Failed(AccessLevelOutcome.AlreadyHeld);
        }

        dbContext.UserAccessLevels.Add(new UserAccessLevel
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccessLevelId = accessLevelId,
            AssignedAt = timeProvider.GetUtcNow(),
            AssignedByUserId = CurrentUser(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        userPermissions.Forget(userId);

        return AccessLevelResult.Ok((await GetAsync(accessLevelId, cancellationToken))!);
    }

    public async Task<AccessLevelResult> WithdrawAsync(
        Guid userId,
        Guid accessLevelId,
        CancellationToken cancellationToken = default)
    {
        UserAccessLevel? assignment = await dbContext.UserAccessLevels.FirstOrDefaultAsync(
            candidate => candidate.UserId == userId && candidate.AccessLevelId == accessLevelId,
            cancellationToken);

        if (assignment is null)
        {
            return AccessLevelResult.Failed(AccessLevelOutcome.NotHeld);
        }

        dbContext.UserAccessLevels.Remove(assignment);
        await dbContext.SaveChangesAsync(cancellationToken);

        userPermissions.Forget(userId);

        return new AccessLevelResult { Outcome = AccessLevelOutcome.Succeeded };
    }

    private async Task<AccessLevelOutcome?> ValidateAsync(
        AccessLevelRequest request,
        Guid? excludingId,
        CancellationToken cancellationToken)
    {
        string name = request.Name.Trim();

        bool duplicate = await dbContext.AccessLevels.AnyAsync(
            level => level.Name.ToUpper() == name.ToUpper(null) && level.Id != excludingId,
            cancellationToken);

        if (duplicate)
        {
            return AccessLevelOutcome.DuplicateName;
        }

        // A module the catalogue does not contain can never be evaluated, so storing
        // it would be a permission that silently does nothing for ever.
        bool unknown = request.Permissions.Any(permission =>
            !ModuleCatalogue.All.Contains(permission.Module, StringComparer.OrdinalIgnoreCase));

        return unknown ? AccessLevelOutcome.UnknownModule : null;
    }

    // Expanded on save so the stored row explains its own grants. An audit reading
    // "approve" without "view" would have no way to know the evaluator adds it.
    private static IReadOnlyList<ModulePermission> Expand(
        IReadOnlyList<ModulePermission> permissions) =>
        [.. permissions.Select(permission => permission with
        {
            Actions = permission.Access
                ? ActionDependencies.Expand(permission.Actions)
                : [],
        })];

    private async Task ForgetHoldersAsync(Guid accessLevelId, CancellationToken cancellationToken)
    {
        List<Guid> holders = await dbContext.UserAccessLevels
            .Where(assignment => assignment.AccessLevelId == accessLevelId)
            .Select(assignment => assignment.UserId)
            .ToListAsync(cancellationToken);

        foreach (Guid holder in holders)
        {
            userPermissions.Forget(holder);
        }
    }

    private IQueryable<AccessLevelDto> Project(IQueryable<AccessLevel> levels) =>
        levels.Select(level => new AccessLevelDto
        {
            Id = level.Id,
            TemplateSlug = level.TemplateSlug,
            Name = level.Name,
            Description = level.Description,
            Kind = level.Kind,
            Status = level.Status,
            Scope = level.Scope,
            Permissions = level.Permissions,
            AssignedCount = dbContext.UserAccessLevels
                .Count(assignment => assignment.AccessLevelId == level.Id),
            LastUsedAt = level.LastUsedAt,
            CreatedAt = level.CreatedAt,
            UpdatedAt = level.UpdatedAt,
        });

    private Guid? CurrentUser() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
