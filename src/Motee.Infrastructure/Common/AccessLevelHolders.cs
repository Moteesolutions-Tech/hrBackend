using Microsoft.EntityFrameworkCore;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Common;

// Who can act for an access level right now.
//
// Read live rather than snapshotted, so somebody who joined this morning can
// clear this morning's queue and somebody who left cannot hold one up. Shared
// by the approvals engine and the workflow engine, which ask the same question
// of the same tables — a second copy is a second answer the first time one of
// them is tightened.
internal static class AccessLevelHolders
{
    public static IQueryable<Guid> UserIds(MoteeDbContext dbContext, Guid accessLevelId) =>
        dbContext.UserAccessLevels
            .AsNoTracking()
            .Where(assignment => assignment.AccessLevelId == accessLevelId)
            .Join(
                dbContext.AccessLevels.Where(level => level.Status == AccessLevelStatus.Active),
                assignment => assignment.AccessLevelId,
                level => level.Id,
                (assignment, _) => assignment.UserId)

            // A holder whose own employee record has gone inactive is a leaver
            // whose access has not been tidied up. Their queue is not somewhere
            // work should sit.
            .Where(userId => !dbContext.Users
                .Any(user => user.Id == userId
                    && user.EmployeeId != null
                    && dbContext.Employees.Any(employee => employee.Id == user.EmployeeId
                        && (employee.Status == EmployeeStatus.Inactive
                            || employee.Status == EmployeeStatus.Deleted))));

    // The employees behind those accounts, narrowed to a department where the
    // caller is scoped to one: a department-scoped workflow should not page the
    // whole company.
    public static async Task<IReadOnlyList<Guid>> EmployeeIdsAsync(
        MoteeDbContext dbContext,
        Guid accessLevelId,
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        IQueryable<Guid> userIds = UserIds(dbContext, accessLevelId);

        IQueryable<Guid> employeeIds = dbContext.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id) && user.EmployeeId != null)
            .Select(user => user.EmployeeId!.Value);

        IQueryable<Employee> employees = dbContext.Employees
            .AsNoTracking()
            .Where(employee =>
                employeeIds.Contains(employee.Id)
                && EmployeeLifecycle.InPost.Contains(employee.Status));

        if (departmentId is Guid department)
        {
            employees = employees.Where(employee => employee.DepartmentId == department);
        }

        return await employees
            .Select(employee => employee.Id)
            .ToListAsync(cancellationToken);
    }
}
