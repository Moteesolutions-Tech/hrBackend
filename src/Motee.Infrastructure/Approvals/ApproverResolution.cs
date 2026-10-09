using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Approvals;

internal sealed class ApproverResolution(MoteeDbContext dbContext) : IApproverResolution
{
    public async Task<ResolvedApprover> ResolveAsync(
        ApproverResolver resolver,
        Guid? subjectEmployeeId,
        Guid? roleId = null,
        CancellationToken cancellationToken = default)
    {
        // A role needs no subject to resolve against — "someone in Finance" means the
        // same thing whoever the request is about — so it is answered before the
        // positional rules reach for their anchor.
        if (resolver == ApproverResolver.Role)
        {
            return await RoleAsync(roleId, subjectEmployeeId, cancellationToken);
        }

        // The positional rules answer "relative to whom". With no subject there is no
        // anchor, and guessing one would put the approval in front of somebody arbitrary.
        if (subjectEmployeeId is not Guid employeeId)
        {
            return ResolvedApprover.Nobody("This approval has no employee to resolve against.");
        }

        return resolver switch
        {
            ApproverResolver.LineManager => await LineManagerAsync(employeeId, cancellationToken),
            ApproverResolver.DepartmentHead => await DepartmentHeadAsync(employeeId, cancellationToken),
            _ => ResolvedApprover.Nobody($"Unsupported approver rule '{resolver}'."),
        };
    }

    public async Task<bool> HoldsRoleAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default) =>
        await EligibleHolders(roleId).AnyAsync(holder => holder == userId, cancellationToken);

    private async Task<ResolvedApprover> RoleAsync(
        Guid? roleId,
        Guid? subjectEmployeeId,
        CancellationToken cancellationToken)
    {
        if (roleId is not Guid role)
        {
            return ResolvedApprover.Nobody("This step names no access level to ask.");
        }

        // A deactivated level grants nothing to anyone still holding it, so a step
        // pointing at one has nobody behind it however many people are assigned.
        AccessLevel? level = await dbContext.AccessLevels
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == role && candidate.Status == AccessLevelStatus.Active,
                cancellationToken);

        if (level is null)
        {
            return ResolvedApprover.Nobody("The access level for this step no longer exists.");
        }

        // Checked now as well as at decision time. A step handed to an empty queue is
        // one nobody will ever see, and saying so at submission is the difference
        // between a chain that stalls visibly and one that stalls silently.
        List<Guid> holders = await EligibleHolders(role).ToListAsync(cancellationToken);

        if (holders.Count == 0)
        {
            return ResolvedApprover.Nobody($"Nobody currently holds {level.Name}.");
        }

        // Somebody approving their own request is not an approval, whether they were
        // named by position or reached through a role. If excluding them empties the
        // queue they were the only holder, and there is genuinely no second pair of eyes.
        if (subjectEmployeeId is Guid subject)
        {
            Guid? theirUserId = await dbContext.Users
                .Where(user => user.EmployeeId == subject)
                .Select(user => (Guid?)user.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (theirUserId is Guid self && holders.All(holder => holder == self))
            {
                return ResolvedApprover.Nobody(
                    $"They are the only person holding {level.Name}.");
            }
        }

        return ResolvedApprover.ToRole(role, level.Name);
    }

    // Everyone who can act on a role step right now. Read live rather than snapshotted,
    // so somebody who joined the team this morning can clear this morning's queue and
    // somebody who left cannot hold one up.
    private IQueryable<Guid> EligibleHolders(Guid roleId) =>
        dbContext.UserAccessLevels
            .AsNoTracking()
            .Where(assignment => assignment.AccessLevelId == roleId)
            .Join(
                dbContext.AccessLevels.Where(level => level.Status == AccessLevelStatus.Active),
                assignment => assignment.AccessLevelId,
                level => level.Id,
                (assignment, _) => assignment.UserId)

            // A holder whose own employee record has gone inactive is a leaver whose
            // access has not been tidied up. Their queue is not somewhere work should sit.
            .Where(userId => !dbContext.Users
                .Any(user => user.Id == userId
                    && user.EmployeeId != null
                    && dbContext.Employees.Any(employee => employee.Id == user.EmployeeId
                        && (employee.Status == Domain.Employees.EmployeeStatus.Inactive
                            || employee.Status == Domain.Employees.EmployeeStatus.Deleted))));

    private async Task<ResolvedApprover> LineManagerAsync(
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        Guid? managerId = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => employee.ManagerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (managerId is not Guid manager)
        {
            return ResolvedApprover.Nobody("No line manager is recorded for this employee.");
        }

        // Approving your own request is not an approval. It happens whenever a manager's
        // own record goes through a chain that asks for their manager and the data has
        // them reporting to themselves — rare, and silent when it is not caught.
        if (manager == employeeId)
        {
            return ResolvedApprover.Nobody("This employee is recorded as their own manager.");
        }

        return await DescribeAsync(manager, cancellationToken);
    }

    private async Task<ResolvedApprover> DepartmentHeadAsync(
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        Guid? departmentId = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => employee.DepartmentId)
            .FirstOrDefaultAsync(cancellationToken);

        if (departmentId is not Guid department)
        {
            return ResolvedApprover.Nobody("This employee is not in a department.");
        }

        Guid? headId = await dbContext.Departments
            .AsNoTracking()
            .Where(candidate => candidate.Id == department)
            .Select(candidate => candidate.HeadEmployeeId)
            .FirstOrDefaultAsync(cancellationToken);

        if (headId is not Guid head)
        {
            return ResolvedApprover.Nobody("No head is recorded for this department.");
        }

        // Same problem as above, one level up: the head of a department approving their
        // own onboarding is not a second pair of eyes.
        if (head == employeeId)
        {
            return ResolvedApprover.Nobody("This employee is the head of their own department.");
        }

        return await DescribeAsync(head, cancellationToken);
    }

    private async Task<ResolvedApprover> DescribeAsync(
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        var found = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => new
            {
                employee.FirstName,
                employee.LastName,
                employee.Status,

                // The account that can actually act. Somebody in the org chart without
                // a login cannot approve anything.
                UserId = dbContext.Users
                    .Where(user => user.EmployeeId == employee.Id)
                    .Select(user => (Guid?)user.Id)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (found is null)
        {
            return ResolvedApprover.Nobody("The recorded approver no longer exists.");
        }

        // A leaver cannot approve. Their record survives for the history, and pointing a
        // live approval at them would leave it waiting on somebody who has gone —
        // which is precisely the queue nobody notices until it is weeks old.
        if (found.Status is Domain.Employees.EmployeeStatus.Inactive
            or Domain.Employees.EmployeeStatus.Deleted)
        {
            return ResolvedApprover.Nobody(
                $"{found.FirstName} {found.LastName} has left and cannot approve.");
        }

        if (found.UserId is null)
        {
            return ResolvedApprover.Nobody(
                $"{found.FirstName} {found.LastName} has no account and cannot approve.");
        }

        return ResolvedApprover.To(
            employeeId, found.UserId, $"{found.FirstName} {found.LastName}");
    }
}
