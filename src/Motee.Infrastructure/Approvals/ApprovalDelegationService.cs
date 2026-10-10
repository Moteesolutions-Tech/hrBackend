using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Approvals;
using Motee.Domain.Employees;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Approvals;

internal sealed class ApprovalDelegationService(
    MoteeDbContext dbContext,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IApprovalDelegationService
{
    public async Task<IReadOnlyList<ApprovalDelegationDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return [];
        }

        return await Project(dbContext.ApprovalDelegations
                .Where(delegation => delegation.DelegatorEmployeeId == me))
            .OrderByDescending(delegation => delegation.StartDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApprovalDelegationDto>> ActiveAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = Today();

        return await Project(dbContext.ApprovalDelegations
                .Where(delegation => delegation.StartDate <= today && delegation.EndDate >= today))
            .OrderBy(delegation => delegation.EndDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<ApprovalDelegationResult> CreateAsync(
        ApprovalDelegationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return ApprovalDelegationResult.Failed(ApprovalDelegationOutcome.NoEmployeeRecord);
        }

        DelegationRules.Rejection rejection = DelegationRules.Check(
            request.StartDate, request.EndDate, me, request.DelegateEmployeeId);

        if (rejection != DelegationRules.Rejection.None)
        {
            return ApprovalDelegationResult.Failed(rejection switch
            {
                DelegationRules.Rejection.EndBeforeStart =>
                    ApprovalDelegationOutcome.EndBeforeStart,
                DelegationRules.Rejection.TooLong => ApprovalDelegationOutcome.TooLong,
                _ => ApprovalDelegationOutcome.DelegatingToSelf,
            });
        }

        // Must be somebody who can actually act. Routing a queue to a leaver is how it
        // ages for a month before anybody notices.
        var delegate_ = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == request.DelegateEmployeeId)
            .Select(employee => new
            {
                employee.Status,
                HasAccount = dbContext.Users.Any(user => user.EmployeeId == employee.Id),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (delegate_ is null)
        {
            return ApprovalDelegationResult.Failed(ApprovalDelegationOutcome.UnknownDelegate);
        }

        if (delegate_.Status is EmployeeStatus.Inactive or EmployeeStatus.Deleted
            || !delegate_.HasAccount)
        {
            return ApprovalDelegationResult.Failed(
                ApprovalDelegationOutcome.DelegateUnavailable);
        }

        // Two arrangements covering the same day give the resolver nothing to choose
        // between, and choosing arbitrarily would be worse than refusing here.
        bool overlaps = await dbContext.ApprovalDelegations.AnyAsync(
            existing => existing.DelegatorEmployeeId == me
                && existing.StartDate <= request.EndDate
                && existing.EndDate >= request.StartDate,
            cancellationToken);

        if (overlaps)
        {
            return ApprovalDelegationResult.Failed(ApprovalDelegationOutcome.Overlapping);
        }

        ApprovalDelegation delegation = new()
        {
            Id = Guid.NewGuid(),
            DelegatorEmployeeId = me,
            DelegateEmployeeId = request.DelegateEmployeeId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CreatedByUserId = Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        dbContext.ApprovalDelegations.Add(delegation);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ApprovalDelegationResult.Ok(
            await Project(dbContext.ApprovalDelegations
                    .Where(candidate => candidate.Id == delegation.Id))
                .FirstAsync(cancellationToken));
    }

    public async Task<ApprovalDelegationOutcome> CancelAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ApprovalDelegation? delegation = await dbContext.ApprovalDelegations
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (delegation is null)
        {
            return ApprovalDelegationOutcome.NotFound;
        }

        // Only the person who arranged it. An administrator cancelling somebody's cover
        // while they are away is how a queue silently stops being watched, and this panel
        // is theirs.
        if (await currentEmployee.IdAsync(cancellationToken) != delegation.DelegatorEmployeeId)
        {
            return ApprovalDelegationOutcome.NotTheirs;
        }

        // Removed outright. Steps it already redirected carry their own copy of the
        // period and the reason, so cancelling cannot make a past decision unexplainable.
        dbContext.ApprovalDelegations.Remove(delegation);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ApprovalDelegationOutcome.Succeeded;
    }

    private IQueryable<ApprovalDelegationDto> Project(IQueryable<ApprovalDelegation> delegations)
    {
        DateOnly today = Today();

        return delegations.AsNoTracking().Select(delegation => new ApprovalDelegationDto
        {
            Id = delegation.Id,
            DelegatorEmployeeId = delegation.DelegatorEmployeeId,
            DelegatorName = dbContext.Employees
                .Where(employee => employee.Id == delegation.DelegatorEmployeeId)
                .Select(employee => employee.FirstName + " " + employee.LastName)
                .FirstOrDefault() ?? "Unknown",
            DelegateEmployeeId = delegation.DelegateEmployeeId,
            DelegateName = dbContext.Employees
                .Where(employee => employee.Id == delegation.DelegateEmployeeId)
                .Select(employee => employee.FirstName + " " + employee.LastName)
                .FirstOrDefault() ?? "Unknown",
            StartDate = delegation.StartDate,
            EndDate = delegation.EndDate,
            Reason = delegation.Reason,
            IsActive = delegation.StartDate <= today && delegation.EndDate >= today,
            CreatedAt = delegation.CreatedAt,
        });
    }

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
}
