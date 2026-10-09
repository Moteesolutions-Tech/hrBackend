using Microsoft.EntityFrameworkCore;
using Motee.Application.Authorization;
using Motee.Application.Tenancy;
using Motee.Domain.Authorization;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Authorization;

internal sealed class TenantUserService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : ITenantUserService
{
    public async Task<IReadOnlyList<TenantUserDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        // Users are deliberately not ITenantScoped, so the global filter does not cover
        // them: login has to find someone by email before any tenant is known, and
        // platform staff belong to none. That makes every query over Users responsible
        // for its own tenant clause, and forgetting it returns every company's people —
        // which is exactly what the first version of this method did.
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return [];
        }

        // Platform staff carry no tenant and operate across all of them. The clause
        // above already excludes them; this states it, so a later change to how the
        // tenant is resolved cannot quietly let them through.
        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.TenantId == tenantId && !user.IsPlatformStaff)
            .OrderBy(user => user.FirstName)
            .ThenBy(user => user.LastName)
            .Select(user => new TenantUserDto
            {
                Id = user.Id,
                Name = user.FirstName + " " + user.LastName,
                Initials = user.FirstName.Substring(0, 1) + user.LastName.Substring(0, 1),
                Email = user.Email!,
                IsOwner = user.IsOwner,
                EmployeeId = user.EmployeeId,

                // Read through from the employee record rather than stored on the user,
                // so a promotion or a transfer shows here without a second write.
                JobTitle = dbContext.Employees
                    .Where(employee => employee.Id == user.EmployeeId)
                    .Select(employee => employee.JobTitle)
                    .FirstOrDefault(),

                DepartmentName = dbContext.Employees
                    .Where(employee => employee.Id == user.EmployeeId)
                    .SelectMany(employee => dbContext.Departments
                        .Where(department => department.Id == employee.DepartmentId)
                        .Select(department => department.Name))
                    .FirstOrDefault(),

                AccessLevels = dbContext.UserAccessLevels
                    .Where(assignment => assignment.UserId == user.Id)
                    .SelectMany(assignment => dbContext.AccessLevels
                        .Where(level => level.Id == assignment.AccessLevelId)
                        .Select(level => new HeldAccessLevelDto
                        {
                            Id = level.Id,
                            Name = level.Name,

                            // Draft counts as inactive here: neither grants anything,
                            // and the screen only needs to say whether this assignment
                            // currently does something.
                            IsActive = level.Status == AccessLevelStatus.Active,
                        }))
                    .OrderBy(level => level.Name)
                    .ToList(),

                // Order matters. Someone unverified who has also tripped the lockout is
                // reported as Pending, because verifying is what they have to do first
                // and a lockout that expires on its own would otherwise hide it.
                State = !user.EmailConfirmed
                    ? UserAccountState.Pending
                    : user.LockoutEnd > now
                        ? UserAccountState.Locked
                        : UserAccountState.Active,

                LockedUntil = user.LockoutEnd > now ? user.LockoutEnd : null,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
            })
            .ToListAsync(cancellationToken);
    }
}
