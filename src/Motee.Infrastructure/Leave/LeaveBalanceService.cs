using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Leave;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Leave;

internal sealed class LeaveBalanceService(
    MoteeDbContext dbContext,
    LeaveYearResolver leaveYears,
    IRequestContext requestContext,
    TimeProvider timeProvider) : ILeaveBalanceService
{
    public async Task<LeaveBalanceDto?> ForAsync(
        Guid employeeId,
        Guid leaveTypeId,
        DateOnly? asAt = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LeaveBalanceDto> balances = await BuildAsync(
            [employeeId], leaveTypeId, asAt, cancellationToken);

        return balances.FirstOrDefault();
    }

    public async Task<IReadOnlyList<LeaveBalanceDto>> ForEmployeeAsync(
        Guid employeeId,
        DateOnly? asAt = null,
        CancellationToken cancellationToken = default) =>
        await BuildAsync([employeeId], null, asAt, cancellationToken);

    public async Task<PagedResult<LeaveBalanceDto>> ListAsync(
        LeaveBalanceQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Domain.Employees.Employee> people = dbContext.Employees
            .Where(employee => employee.Status != Domain.Employees.EmployeeStatus.Deleted);

        if (query.DepartmentId is Guid departmentId)
        {
            people = people.Where(employee => employee.DepartmentId == departmentId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string term = $"%{query.Search.Trim()}%";

            people = people.Where(employee =>
                EF.Functions.ILike(employee.FirstName + " " + employee.LastName, term)
                || EF.Functions.ILike(employee.Email, term));
        }

        int total = await people.CountAsync(cancellationToken);

        // Paged by person, not by balance row. A page of twenty-five people showing six
        // leave types each is what the screen wants; paging the product of the two would
        // split somebody's balances across two pages.
        List<Guid> page = await people
            .OrderBy(employee => employee.FirstName)
            .ThenBy(employee => employee.LastName)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(employee => employee.Id)
            .ToListAsync(cancellationToken);

        return new PagedResult<LeaveBalanceDto>
        {
            Items = await BuildAsync(page, query.LeaveTypeId, query.AsAt, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<LeaveAdjustmentResult> AdjustAsync(
        LeaveAdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Days == 0m)
        {
            return LeaveAdjustmentResult.Failed(LeaveAdjustmentOutcome.NoChange);
        }

        bool employeeExists = await dbContext.Employees
            .AnyAsync(employee => employee.Id == request.EmployeeId, cancellationToken);

        if (!employeeExists)
        {
            return LeaveAdjustmentResult.Failed(LeaveAdjustmentOutcome.NotFound);
        }

        bool typeExists = await dbContext.LeaveTypes
            .AnyAsync(type => type.Id == request.LeaveTypeId && type.IsActive, cancellationToken);

        if (!typeExists)
        {
            return LeaveAdjustmentResult.Failed(LeaveAdjustmentOutcome.UnknownLeaveType);
        }

        DateOnly asAt = request.AsAt ?? Today();
        LeaveYear year = await leaveYears.ForAsync(asAt, cancellationToken);

        dbContext.LeaveAdjustments.Add(new LeaveAdjustment
        {
            Id = Guid.NewGuid(),
            EmployeeId = request.EmployeeId,
            LeaveTypeId = request.LeaveTypeId,
            LeaveYearStart = year.Start,
            Days = request.Days,
            Reason = request.Reason.Trim(),
            CreatedByUserId = CurrentUser(),
            CreatedAt = timeProvider.GetUtcNow(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        LeaveBalanceDto? balance = await ForAsync(
            request.EmployeeId, request.LeaveTypeId, asAt, cancellationToken);

        return balance is null
            ? LeaveAdjustmentResult.Failed(LeaveAdjustmentOutcome.NotFound)
            : LeaveAdjustmentResult.Ok(balance);
    }

    // One pass over a set of people, in four queries rather than four per person per
    // leave type. The HR balances tab is the densest screen in the product — a company of
    // 200 with six leave types is 1,200 rows, and a query each would be unusable.
    private async Task<IReadOnlyList<LeaveBalanceDto>> BuildAsync(
        IReadOnlyList<Guid> employeeIds,
        Guid? leaveTypeId,
        DateOnly? asAt,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        DateOnly on = asAt ?? Today();
        LeaveYear year = await leaveYears.ForAsync(on, cancellationToken);

        List<PolicyRow> policies = await dbContext.LeavePolicies
            .AsNoTracking()
            .Where(policy => policy.IsActive
                && (leaveTypeId == null || policy.LeaveTypeId == leaveTypeId))
            .Join(
                dbContext.LeaveTypes.Where(type => type.IsActive),
                policy => policy.LeaveTypeId,
                type => type.Id,
                (policy, type) => new PolicyRow
                {
                    LeaveTypeId = type.Id,
                    LeaveTypeName = type.Name,
                    Sequence = type.Sequence,
                    DaysPerYear = policy.DaysPerYear,
                    AccruesMonthly = policy.AccruesMonthly,
                    CarryOverExpiryMonths = policy.CarryOverExpiryMonths,
                })
            .OrderBy(row => row.Sequence)
            .ToListAsync(cancellationToken);

        if (policies.Count == 0)
        {
            return [];
        }

        List<PersonRow> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new PersonRow
            {
                Id = employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                StartDate = employee.StartDate,
                DepartmentName = dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // Booked days, split by whether they are certain. Grouped in the database: this
        // is the sum the whole feature turns on, and pulling every request back to add
        // them up in memory would be the one query that does not scale.
        var booked = await dbContext.LeaveRequests
            .AsNoTracking()
            .Where(request => employeeIds.Contains(request.EmployeeId)
                && request.LeaveYearStart == year.Start
                && (request.Status == LeaveRequestStatus.Approved
                    || request.Status == LeaveRequestStatus.Pending))
            .GroupBy(request => new { request.EmployeeId, request.LeaveTypeId, request.Status })
            .Select(group => new
            {
                group.Key.EmployeeId,
                group.Key.LeaveTypeId,
                group.Key.Status,
                Days = group.Sum(request => request.TotalDays),
            })
            .ToListAsync(cancellationToken);

        var adjustments = await dbContext.LeaveAdjustments
            .AsNoTracking()
            .Where(adjustment => employeeIds.Contains(adjustment.EmployeeId)
                && adjustment.LeaveYearStart == year.Start)
            .GroupBy(adjustment => new { adjustment.EmployeeId, adjustment.LeaveTypeId })
            .Select(group => new
            {
                group.Key.EmployeeId,
                group.Key.LeaveTypeId,
                Days = group.Sum(adjustment => adjustment.Days),
            })
            .ToListAsync(cancellationToken);

        List<LeaveCarryOver> carried = await dbContext.LeaveCarryOvers
            .AsNoTracking()
            .Where(carry => employeeIds.Contains(carry.EmployeeId)
                && carry.LeaveYearStart == year.Start)
            .ToListAsync(cancellationToken);

        List<LeaveBalanceDto> results = [];

        foreach (PersonRow person in people.OrderBy(row => row.Name))
        {
            foreach (PolicyRow policy in policies)
            {
                decimal used = booked
                    .Where(row => row.EmployeeId == person.Id
                        && row.LeaveTypeId == policy.LeaveTypeId
                        && row.Status == LeaveRequestStatus.Approved)
                    .Sum(row => row.Days);

                decimal pending = booked
                    .Where(row => row.EmployeeId == person.Id
                        && row.LeaveTypeId == policy.LeaveTypeId
                        && row.Status == LeaveRequestStatus.Pending)
                    .Sum(row => row.Days);

                decimal adjusted = adjustments
                    .Where(row => row.EmployeeId == person.Id
                        && row.LeaveTypeId == policy.LeaveTypeId)
                    .Sum(row => row.Days);

                LeaveCarryOver? carry = carried.FirstOrDefault(row =>
                    row.EmployeeId == person.Id && row.LeaveTypeId == policy.LeaveTypeId);

                // A policy that grants the year up front is not accrual — somebody with
                // 25 days has 25 in January, and pro-rating them would be inventing a
                // rule the company did not set.
                decimal accrued = policy.AccruesMonthly
                    ? LeaveEntitlement.AccruedBy(
                        policy.DaysPerYear, year, on, person.StartDate)
                    : policy.DaysPerYear;

                decimal carriedOver = carry?.Days ?? 0m;

                decimal carriedAvailable = LeaveEntitlement.CarriedOverAvailable(
                    carriedOver, year, policy.CarryOverExpiryMonths, on);

                results.Add(new LeaveBalanceDto
                {
                    EmployeeId = person.Id,
                    EmployeeName = person.Name,
                    DepartmentName = person.DepartmentName,
                    LeaveTypeId = policy.LeaveTypeId,
                    LeaveTypeName = policy.LeaveTypeName,
                    Entitlement = policy.DaysPerYear,
                    Accrued = accrued,
                    CarriedOver = carriedOver,
                    CarriedOverAvailable = carriedAvailable,
                    CarryOverExpiresOn = carry?.ExpiresOn,
                    Used = used,
                    Pending = pending,
                    Adjustments = adjusted,
                    Available = LeaveEntitlement.Available(
                        accrued, carriedAvailable, used, pending, adjusted),
                    LeaveYearLabel = year.Label,
                    LeaveYearStart = year.Start,
                });
            }
        }

        return results;
    }

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    private Guid? CurrentUser() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private sealed record PolicyRow
    {
        public Guid LeaveTypeId { get; init; }

        public string LeaveTypeName { get; init; } = string.Empty;

        public int Sequence { get; init; }

        public decimal DaysPerYear { get; init; }

        public bool AccruesMonthly { get; init; }

        public int CarryOverExpiryMonths { get; init; }
    }

    private sealed record PersonRow
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public DateOnly? StartDate { get; init; }

        public string? DepartmentName { get; init; }
    }
}
