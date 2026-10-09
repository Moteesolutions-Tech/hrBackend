using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Leave;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Leave;

internal sealed class LeaveYearEndService(
    MoteeDbContext dbContext,
    ILeaveBalanceService balances,
    LeaveYearResolver leaveYears,
    TimeProvider timeProvider) : ILeaveYearEndService
{
    public async Task<bool> IsFirstDayOfYearAsync(
        DateOnly today,
        CancellationToken cancellationToken = default) =>
        (await leaveYears.ForAsync(today, cancellationToken)).Start == today;

    public Task<LeaveYearEndResult> PreviewAsync(
        DateOnly? yearContaining = null,
        CancellationToken cancellationToken = default) =>
        ComputeAsync(yearContaining, apply: false, cancellationToken);

    public Task<LeaveYearEndResult> CloseAsync(
        DateOnly? yearContaining = null,
        CancellationToken cancellationToken = default) =>
        ComputeAsync(yearContaining, apply: true, cancellationToken);

    private async Task<LeaveYearEndResult> ComputeAsync(
        DateOnly? yearContaining,
        bool apply,
        CancellationToken cancellationToken)
    {
        DateOnly within = yearContaining
            ?? DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        LeaveYear closing = await leaveYears.ForAsync(within, cancellationToken);
        LeaveYear next = await leaveYears.ForAsync(closing.End.AddDays(1), cancellationToken);

        // Only types that bank days take part. A per-occasion entitlement has nothing to
        // carry and nothing to lose, and counting it would report hundreds of days lapsed
        // that nobody was ever going to take.
        List<PolicyRow> policies = await dbContext.LeaveTypes
            .AsNoTracking()
            .Join(
                dbContext.LeavePolicies.Where(policy => policy.IsActive && policy.TracksBalance),
                type => type.Id,
                policy => policy.LeaveTypeId,
                (type, policy) => new PolicyRow
                {
                    LeaveTypeId = type.Id,
                    CarryOverAllowed = policy.CarryOverAllowed,
                    MaxCarryOverDays = policy.MaxCarryOverDays,
                    ExpiryMonths = policy.CarryOverExpiryMonths,
                })
            .ToListAsync(cancellationToken);

        // Asked before anything is computed, because the answer changes what a re-run
        // means: a year already closed reports what it wrote rather than recalculating it.
        bool alreadyClosed = await dbContext.LeaveCarryOvers
            .AnyAsync(carry => carry.LeaveYearStart == next.Start, cancellationToken);

        List<LeaveCarryOverDto> carried = [];
        decimal lapsed = 0m;

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (PolicyRow policy in policies)
        {
            // Balances are read at the closing year's last day, not today. Reading them as
            // at today would value a December closure against January's fresh entitlement,
            // so nobody would carry anything and the run would look like it had worked.
            IReadOnlyList<LeaveBalanceDto> rows = await AllBalancesAsync(
                policy.LeaveTypeId, closing.End, cancellationToken);

            foreach (LeaveBalanceDto balance in rows)
            {
                if (balance.Available <= 0m)
                {
                    continue;
                }

                decimal days = LeaveEntitlement.CarryForward(
                    balance.Available, policy.CarryOverAllowed, policy.MaxCarryOverDays);

                lapsed += balance.Available - days;

                if (days <= 0m)
                {
                    continue;
                }

                DateOnly? expiresOn = policy.ExpiryMonths > 0
                    ? next.Start.AddMonths(policy.ExpiryMonths)
                    : null;

                carried.Add(new LeaveCarryOverDto
                {
                    EmployeeId = balance.EmployeeId,
                    EmployeeName = balance.EmployeeName,
                    LeaveTypeId = policy.LeaveTypeId,
                    LeaveTypeName = balance.LeaveTypeName,
                    Available = balance.Available,
                    Carried = days,
                    Lapsed = balance.Available - days,
                    ExpiresOn = expiresOn,
                });

                if (apply)
                {
                    await WriteAsync(balance, policy, next, days, expiresOn, now, cancellationToken);
                }
            }
        }

        if (apply)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new LeaveYearEndResult
        {
            ClosedYearLabel = closing.Label,
            ClosedYearStart = closing.Start,
            NextYearStart = next.Start,
            CarriedOver = carried,
            DaysLapsed = lapsed,
            AlreadyClosed = alreadyClosed,
            Applied = apply,
        };
    }

    // Writes only what is not there. A row that already exists is left exactly as it was,
    // never recalculated: by the time a second run happens somebody may have started
    // spending those days, and moving the figure underneath a booking they have already
    // made is worse than leaving a stale one.
    //
    // The unique index on (tenant, employee, type, year) is the backstop; this is what
    // stops the insert being attempted at all.
    private async Task WriteAsync(
        LeaveBalanceDto balance,
        PolicyRow policy,
        LeaveYear next,
        decimal days,
        DateOnly? expiresOn,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool exists = await dbContext.LeaveCarryOvers.AnyAsync(
            carry => carry.EmployeeId == balance.EmployeeId
                && carry.LeaveTypeId == policy.LeaveTypeId
                && carry.LeaveYearStart == next.Start,
            cancellationToken);

        if (exists)
        {
            return;
        }

        dbContext.LeaveCarryOvers.Add(new LeaveCarryOver
        {
            Id = Guid.NewGuid(),
            EmployeeId = balance.EmployeeId,
            LeaveTypeId = policy.LeaveTypeId,
            LeaveYearStart = next.Start,
            Days = days,
            ExpiresOn = expiresOn,
            CreatedAt = now,
        });
    }

    // Every employee's balance for one type. Paged through rather than taken in one query:
    // a company with four thousand staff is an ordinary size, and the page cap exists so a
    // screen cannot ask for all of them at once. This is the one caller that genuinely
    // needs to.
    private async Task<IReadOnlyList<LeaveBalanceDto>> AllBalancesAsync(
        Guid leaveTypeId,
        DateOnly asAt,
        CancellationToken cancellationToken)
    {
        List<LeaveBalanceDto> all = [];
        int page = 1;

        while (true)
        {
            PagedResult<LeaveBalanceDto> batch = await balances.ListAsync(
                new LeaveBalanceQuery
                {
                    LeaveTypeId = leaveTypeId,
                    AsAt = asAt,
                    Page = page,
                    PageSize = PagedQuery.MaxPageSize,
                },
                cancellationToken);

            all.AddRange(batch.Items);

            if (!batch.HasNextPage)
            {
                return all;
            }

            page++;
        }
    }

    private sealed record PolicyRow
    {
        public required Guid LeaveTypeId { get; init; }

        public required bool CarryOverAllowed { get; init; }

        public required decimal MaxCarryOverDays { get; init; }

        public required int ExpiryMonths { get; init; }
    }
}
