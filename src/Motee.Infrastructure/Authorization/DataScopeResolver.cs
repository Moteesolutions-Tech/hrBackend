using Microsoft.EntityFrameworkCore;
using Motee.Application.Authorization;
using Motee.Domain.Authorization;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Authorization;

internal sealed class DataScopeResolver(MoteeDbContext dbContext) : IDataScopeResolver
{
    public async Task<DataScope> ResolveAsync(
        DataScope scope,
        Guid? holderEmployeeId,
        CancellationToken cancellationToken = default)
    {
        if (!scope.NeedsHolder)
        {
            return scope;
        }

        // No employee record — the admin who registered the tenant before anybody was
        // hired, or a platform operator. There is nothing to be relative to, so the scope
        // reaches nothing rather than everything.
        if (holderEmployeeId is not Guid employeeId)
        {
            return Empty(scope.Kind);
        }

        var holder = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => new { employee.DepartmentId, employee.BranchId })
            .FirstOrDefaultAsync(cancellationToken);

        if (holder is null)
        {
            return Empty(scope.Kind);
        }

        return scope.Kind switch
        {
            DataScopeKind.OwnDepartment => holder.DepartmentId is Guid department
                ? DataScope.Departments(department)
                : DataScope.Departments(),

            DataScopeKind.OwnBranch => holder.BranchId is Guid branch
                ? DataScope.Branches(branch)
                : DataScope.Branches(),

            _ => scope,
        };
    }

    // The named equivalent with nothing in it. Deliberately not DataScope.Nothing: the
    // editor and the audit trail should still be able to say which axis the level was
    // scoped along, even when it currently reaches nobody.
    private static DataScope Empty(DataScopeKind kind) => kind switch
    {
        DataScopeKind.OwnDepartment => DataScope.Departments(),
        DataScopeKind.OwnBranch => DataScope.Branches(),
        _ => DataScope.Nothing,
    };
}
