using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Authorization;

// Which employees a viewer's scope reaches.
//
// One copy of the rule, on purpose. This logic began life inside EmployeeService, which
// was the only register that narrowed at all — every other one handed its query straight
// to the database, so a department-scoped HR user reading the contract register got the
// whole company's pay. Writing the rule out again in each of those services would be
// fourteen chances to spell it differently, and the one that drifts is a leak nobody
// sees, because the screen still looks right.
//
// Composed as a subquery rather than materialised into a list of ids. A tenant with
// 5,000 employees would otherwise mean a round trip and a 5,000-element IN clause on
// every page of every register; as a subquery it stays one statement, and the database
// can use the indexes on department_id and manager_id.
//
// Each register then narrows itself with three lines, rather than this class trying to
// do it generically — the employee is reached differently on each one, and an expression
// built by hand to paper over that would be harder to read than the three lines:
//
//     if (!viewer.ReachesEveryone)
//     {
//         IQueryable<Guid> reachable = reach.IdsOf(viewer);
//         claims = claims.Where(claim => reachable.Contains(claim.EmployeeId));
//     }
internal sealed class EmployeeReach(MoteeDbContext dbContext)
{
    // Self-relative scopes are not handled here and do not need to be: the permission
    // handler resolves OwnDepartment and OwnBranch into their named equivalents before
    // any endpoint sees them, so by this point a scope names its departments outright.
    // They fall to the final case and reach nothing, which is the safe way to be wrong.
    public IQueryable<Employee> Of(Viewer viewer) =>
        viewer.Scope.Kind switch
        {
            DataScopeKind.All => dbContext.Employees,

            // The departments the access level names, not the one the viewer happens to
            // be in. Moving someone between departments must not silently change what
            // they can see.
            DataScopeKind.Department => dbContext.Employees.Where(employee =>
                employee.DepartmentId != null
                && viewer.Scope.DepartmentIds.Contains(employee.DepartmentId.Value)),

            // Everyone posted to the named sites. Read straight off the employee rather
            // than through the department, because the whole point of this axis is that
            // it cuts across departments — Engineering is in Lagos and London both.
            DataScopeKind.Branch => dbContext.Employees.Where(employee =>
                employee.BranchId != null
                && viewer.Scope.BranchIds.Contains(employee.BranchId.Value)),

            // An employee belongs to a department, and the department to a unit, so this
            // reaches everyone under the named units.
            DataScopeKind.BusinessUnit => dbContext.Employees.Where(employee =>
                employee.DepartmentId != null
                && dbContext.Departments.Any(department =>
                    department.Id == employee.DepartmentId
                    && department.BusinessUnitId != null
                    && viewer.Scope.BusinessUnitIds.Contains(department.BusinessUnitId.Value))),

            // Their reports and themselves — a manager needs their own row in the list
            // they work from.
            //
            // The holder is matched out of the scope first, and that is not tidiness.
            // Comparing ManagerId against a null holder asks the database for everybody
            // whose manager is null, which is every employee at the top of the tree —
            // so a DirectReports grant reaching this with no employee behind it would
            // answer with the executives. A token without the employee claim, or a
            // background caller, is exactly when that happens.
            DataScopeKind.DirectReports when viewer.EmployeeId is Guid manager =>
                dbContext.Employees.Where(employee =>
                    employee.ManagerId == manager || employee.Id == manager),

            DataScopeKind.Self when viewer.EmployeeId is Guid self =>
                dbContext.Employees.Where(employee => employee.Id == self),

            // Everything else reaches nobody: a holder-relative scope with no holder, a
            // self-relative kind that was never resolved, None.
            _ => dbContext.Employees.Where(_ => false),
        };

    public IQueryable<Guid> IdsOf(Viewer viewer) => Of(viewer).Select(employee => employee.Id);
}
