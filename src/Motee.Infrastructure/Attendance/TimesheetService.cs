using Microsoft.EntityFrameworkCore;
using Motee.Application.Attendance;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Attendance;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Attendance;

internal sealed class TimesheetService(
    MoteeDbContext dbContext,
    IAttendanceService attendance,
    ICurrentEmployee currentEmployee,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : ITimesheetService
{
    public async Task<TimesheetDto?> MineAsync(
        DateOnly? weekStart = null,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return null;
        }

        DateOnly monday = TenantClock.WeekStart(weekStart ?? await TodayAsync(cancellationToken));

        Timesheet? stored = await dbContext.Timesheets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                sheet => sheet.EmployeeId == employeeId && sheet.WeekStart == monday,
                cancellationToken);

        // Pending rather than absent when nobody has handed it in. The screen draws
        // the week either way, and creating a row on every read would write one for
        // every employee for every week they never worked.
        return await DescribeAsync(stored, employeeId, monday, cancellationToken);
    }

    public async Task<TimesheetResult> SubmitAsync(
        DateOnly weekStart,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return TimesheetResult.Failed(TimesheetOutcome.EmployeeNotFound);
        }

        DateOnly monday = TenantClock.WeekStart(weekStart);
        DateOnly today = await TodayAsync(cancellationToken);

        // Handing in Thursday's week on Thursday means asking somebody to approve
        // days that have not happened. The week has to be over.
        if (monday.AddDays(6) >= today)
        {
            return TimesheetResult.Failed(TimesheetOutcome.WeekNotOver);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Timesheet? sheet = await dbContext.Timesheets
            .FirstOrDefaultAsync(
                candidate => candidate.EmployeeId == employeeId && candidate.WeekStart == monday,
                cancellationToken);

        if (sheet is null)
        {
            sheet = new Timesheet
            {
                Id = Guid.NewGuid(),
                EmployeeId = employeeId,
                WeekStart = monday,
                Status = TimesheetStatus.Pending,
                CreatedAt = now,
            };

            dbContext.Timesheets.Add(sheet);
        }

        if (!TimesheetLifecycle.Can(sheet.Status, TimesheetAction.Submit))
        {
            return TimesheetResult.Failed(
                TimesheetOutcome.NotAllowed,
                TimesheetLifecycle.Refusal(sheet.Status, TimesheetAction.Submit));
        }

        sheet.Status = TimesheetStatus.Submitted;
        sheet.SubmittedAt = now;
        sheet.UpdatedAt = now;

        // Cleared on resubmission. A week handed back in still showing why it was
        // sent back the first time reads as though it was rejected again.
        sheet.RejectionReason = null;
        sheet.ResolvedAt = null;
        sheet.ResolvedByUserId = null;

        await dbContext.SaveChangesAsync(cancellationToken);

        return TimesheetResult.Ok(
            (await DescribeAsync(sheet, employeeId, monday, cancellationToken))!);
    }

    public async Task<PagedResult<TimesheetDto>> ListAsync(
        TimesheetQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Timesheet> source = dbContext.Timesheets.AsNoTracking();

        if (query.WeekStart is DateOnly week)
        {
            source = source.Where(sheet => sheet.WeekStart == TenantClock.WeekStart(week));
        }

        if (query.EmployeeId is Guid employeeId)
        {
            source = source.Where(sheet => sheet.EmployeeId == employeeId);
        }

        if (query.Status is TimesheetStatus status)
        {
            source = source.Where(sheet => sheet.Status == status);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            source = source.Where(sheet => dbContext.Employees
                .Any(employee => employee.Id == sheet.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        int total = await source.CountAsync(cancellationToken);

        List<Timesheet> page = await source
            // Oldest submission first: the queue is a queue, and the week somebody
            // has been waiting longest on is the one to review next.
            .OrderBy(sheet => sheet.SubmittedAt ?? DateTimeOffset.MaxValue)
            .ThenByDescending(sheet => sheet.WeekStart)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        List<TimesheetDto> items = [];

        foreach (Timesheet sheet in page)
        {
            items.Add((await DescribeAsync(
                sheet, sheet.EmployeeId, sheet.WeekStart, cancellationToken))!);
        }

        return new PagedResult<TimesheetDto>
        {
            Items = items,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<TimesheetDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Timesheet? sheet = await dbContext.Timesheets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return sheet is null
            ? null
            : await DescribeAsync(sheet, sheet.EmployeeId, sheet.WeekStart, cancellationToken);
    }

    public Task<TimesheetResult> ApproveAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(id, TimesheetAction.Approve, null, cancellationToken);

    public Task<TimesheetResult> RejectAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(reason)
            ? Task.FromResult(TimesheetResult.Failed(TimesheetOutcome.ReasonRequired))
            : ResolveAsync(id, TimesheetAction.Reject, reason.Trim(), cancellationToken);

    private async Task<TimesheetResult> ResolveAsync(
        Guid id,
        TimesheetAction action,
        string? reason,
        CancellationToken cancellationToken)
    {
        Timesheet? sheet = await dbContext.Timesheets
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (sheet is null)
        {
            return TimesheetResult.Failed(TimesheetOutcome.NotFound);
        }

        if (!TimesheetLifecycle.Can(sheet.Status, action))
        {
            return TimesheetResult.Failed(
                TimesheetOutcome.NotAllowed, TimesheetLifecycle.Refusal(sheet.Status, action));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        sheet.Status = action == TimesheetAction.Approve
            ? TimesheetStatus.Approved
            : TimesheetStatus.Rejected;

        sheet.ResolvedAt = now;
        sheet.ResolvedByUserId =
            Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;
        sheet.RejectionReason = reason;
        sheet.UpdatedAt = now;

        if (action == TimesheetAction.Approve)
        {
            // Captured here and never recomputed. What somebody approved is a fact
            // about a moment — a day corrected next week must not reach back and
            // change the number that was signed off.
            IReadOnlyList<AttendanceDayDto> days = await DaysAsync(
                sheet.EmployeeId, sheet.WeekStart, cancellationToken);

            sheet.ApprovedTotalHours = Math.Round(days.Sum(day => day.WorkedHours), 2);
            sheet.ApprovedOvertimeHours = Math.Round(days.Sum(day => day.OvertimeHours), 2);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return TimesheetResult.Ok(
            (await DescribeAsync(sheet, sheet.EmployeeId, sheet.WeekStart, cancellationToken))!);
    }

    private Task<IReadOnlyList<AttendanceDayDto>> DaysAsync(
        Guid employeeId,
        DateOnly weekStart,
        CancellationToken cancellationToken) =>
        attendance.DaysForAsync(employeeId, weekStart, weekStart.AddDays(6), cancellationToken);

    // The week, from the days underneath plus whatever lifecycle exists.
    //
    // Nothing numeric comes out of the timesheet row: a corrected day has to change
    // the week that contains it, and a second copy of the hours here is a copy that
    // stops agreeing.
    private async Task<TimesheetDto?> DescribeAsync(
        Timesheet? sheet,
        Guid employeeId,
        DateOnly weekStart,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AttendanceDayDto> days =
            await DaysAsync(employeeId, weekStart, cancellationToken);

        if (days.Count == 0)
        {
            return null;
        }

        decimal total = Math.Round(days.Sum(day => day.WorkedHours), 2);
        decimal overtime = Math.Round(days.Sum(day => day.OvertimeHours), 2);

        string? resolvedBy = sheet?.ResolvedByUserId is Guid resolver
            ? await dbContext.Users
                .AsNoTracking()
                .Where(user => user.Id == resolver)
                .Select(user => user.FirstName + " " + user.LastName)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new TimesheetDto
        {
            // Empty until the week has been handed in once, so a client can tell a
            // week with a lifecycle from one that has only ever been worked.
            Id = sheet?.Id ?? Guid.Empty,
            EmployeeId = employeeId,
            EmployeeName = days[0].EmployeeName,
            DepartmentName = days[0].DepartmentName,
            WeekStart = weekStart,
            WeekEnd = weekStart.AddDays(6),
            Status = sheet?.Status ?? TimesheetStatus.Pending,
            TotalHours = total,
            OvertimeHours = overtime,
            DaysPresent = days.Count(day => day.Status is AttendanceStatus.Present
                or AttendanceStatus.Late
                or AttendanceStatus.EarlyDeparture),
            DaysLate = days.Count(day => day.Status == AttendanceStatus.Late),
            DaysAbsent = days.Count(day => day.Status == AttendanceStatus.Absent),
            Days = days,
            SubmittedAt = sheet?.SubmittedAt,
            ResolvedAt = sheet?.ResolvedAt,
            ResolvedByName = resolvedBy,
            RejectionReason = sheet?.RejectionReason,
            ApprovedTotalHours = sheet?.ApprovedTotalHours,
            ApprovedOvertimeHours = sheet?.ApprovedOvertimeHours,

            // The flag payroll needs. A week approved at 37 hours that now reads 41
            // has had a day corrected under the approval, and handing that figure on
            // without saying so is how a pay run goes wrong quietly.
            ChangedSinceApproval = sheet?.ApprovedTotalHours is decimal approved
                && approved != total,
        };
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        return TenantClock.DayOf(timeProvider.GetUtcNow(), settings?.TimeZoneId);
    }
}
