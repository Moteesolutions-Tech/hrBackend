using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Domain.Approvals;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Approvals;

internal sealed class ApproverResolution(MoteeDbContext dbContext) : IApproverResolution
{
    public async Task<ResolvedApprover> ResolveAsync(
        ApproverResolver resolver,
        Guid? subjectEmployeeId,
        CancellationToken cancellationToken = default)
    {
        // Both phase 1 resolvers are positional — they answer "relative to whom". With
        // no subject there is no anchor, and guessing one would put the approval in
        // front of somebody arbitrary.
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
