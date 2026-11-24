using Microsoft.EntityFrameworkCore;
using Motee.Application.Attendance;
using Motee.Application.Audit;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Attendance;
using Motee.Domain.Audit;
using Motee.Domain.Employees;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Attendance;

// The roster.
//
// Empty days come back empty. The client fills them with
// defaultTemplateForEmployee "so the schedule always reads as populated instead
// of empty", which means a manager checking cover reads coverage that does not
// exist — and a gap in a rota is the thing a rota is for.
internal sealed class ShiftService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider timeProvider) : IShiftService
{
    private const string Module = "time-payroll.shifts";

    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<RosterResult> RosterAsync(
        RosterQuery query,
        CancellationToken cancellationToken = default)
    {
        ShiftRefusal refusal = Shifts.CheckRange(query.From, query.To);

        if (refusal != ShiftRefusal.None)
        {
            return RosterResult.Refused(query.From, query.To, refusal);
        }

        IQueryable<Employee> people = dbContext.Employees
            .AsNoTracking()
            .Where(employee => EmployeeLifecycle.InPost.Contains(employee.Status));

        if (query.DepartmentId is Guid departmentId)
        {
            people = people.Where(employee => employee.DepartmentId == departmentId);
        }

        if (query.BranchId is Guid branchId)
        {
            people = people.Where(employee => employee.BranchId == branchId);
        }

        if (query.EmployeeId is Guid employeeId)
        {
            people = people.Where(employee => employee.Id == employeeId);
        }

        return await BuildAsync(
            await people.ToListAsync(cancellationToken),
            query.From,
            query.To,
            query.WithGaps,
            cancellationToken);
    }

    public async Task<RosterResult> MineAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        ShiftRefusal refusal = Shifts.CheckRange(from, to);

        if (refusal != ShiftRefusal.None)
        {
            return RosterResult.Refused(from, to, refusal);
        }

        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return new RosterResult { From = from, To = to, Rows = [] };
        }

        List<Employee> me = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .ToListAsync(cancellationToken);

        return await BuildAsync(me, from, to, withGaps: null, cancellationToken);
    }

    public async Task<ShiftResult> AssignAsync(
        ShiftAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.EmployeeId, cancellationToken);

        if (employee is null)
        {
            return ShiftResult.Refused(ShiftRefusal.EmployeeNotFound);
        }

        WorkSchedule? schedule = await dbContext.WorkSchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.WorkScheduleId, cancellationToken);

        if (schedule is null)
        {
            return ShiftResult.Refused(ShiftRefusal.ScheduleNotFound);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        ShiftAssignment? existing = await dbContext.ShiftAssignments
            .FirstOrDefaultAsync(
                row => row.EmployeeId == request.EmployeeId && row.Date == request.Date,
                cancellationToken);

        ShiftAssignment assignment = existing ?? new ShiftAssignment
        {
            Id = Guid.NewGuid(),
            TenantId = employee.TenantId,
            EmployeeId = request.EmployeeId,
            Date = request.Date,
            CreatedAt = now,
        };

        assignment.WorkScheduleId = request.WorkScheduleId;
        assignment.Note = Trimmed(request.Note);

        // Reassigning an existing day un-cancels it. Putting somebody back on a
        // shift that was called off is the ordinary way a cancellation is
        // reversed, and leaving the cancellation behind would show the new
        // shift as called off.
        assignment.CancelledOn = null;

        assignment.AssignedByUserId = ActorId();
        assignment.UpdatedAt = now;

        // Checked against the existing row's own id, so changing the pattern on
        // a day somebody is already rostered is an edit rather than a clash
        // with itself.
        ShiftRefusal refusal = Shifts.Check(
            assignment, schedule, today, alreadyAssigned: false);

        if (refusal != ShiftRefusal.None)
        {
            return ShiftResult.Refused(refusal);
        }

        if (existing is null)
        {
            dbContext.ShiftAssignments.Add(assignment);
        }

        audit.Record(
            existing is null ? AuditAction.Create : AuditAction.Update,
            Module,
            $"{Name(employee)} rostered onto {schedule.Name} on {request.Date:d MMMM yyyy}",
            entityId: assignment.Id,
            entityType: nameof(ShiftAssignment));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ShiftResult.Ok(
            Describe(assignment, schedule, Name(employee), today, worked: false));
    }

    public async Task<ShiftResult> CancelAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ShiftAssignment? assignment = await dbContext.ShiftAssignments
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (assignment is null)
        {
            return ShiftResult.Failed(ShiftOutcome.NotFound);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        assignment.CancelledOn ??= today;
        assignment.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            $"Shift on {assignment.Date:d MMMM yyyy} cancelled",
            entityId: assignment.Id,
            entityType: nameof(ShiftAssignment));

        await dbContext.SaveChangesAsync(cancellationToken);

        return await ReloadAsync(assignment, today, cancellationToken);
    }

    public async Task<ShiftOutcome> ClearAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ShiftAssignment? assignment = await dbContext.ShiftAssignments
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (assignment is null)
        {
            return ShiftOutcome.NotFound;
        }

        // Removed, which leaves the day unrostered — a different thing from
        // cancelled. Cancelled says somebody was meant to be there and is not;
        // cleared says nobody was ever put down for it.
        dbContext.ShiftAssignments.Remove(assignment);

        audit.Record(
            AuditAction.Delete,
            Module,
            $"Shift on {assignment.Date:d MMMM yyyy} removed from the rota",
            entityId: assignment.Id,
            entityType: nameof(ShiftAssignment));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ShiftOutcome.Succeeded;
    }

    // ── shared ─────────────────────────────────────────────────────────────

    private async Task<RosterResult> BuildAsync(
        IReadOnlyList<Employee> people,
        DateOnly from,
        DateOnly to,
        bool? withGaps,
        CancellationToken cancellationToken)
    {
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<Guid> employeeIds = [.. people.Select(employee => employee.Id)];

        List<ShiftAssignment> assignments = await dbContext.ShiftAssignments
            .AsNoTracking()
            .Where(assignment => employeeIds.Contains(assignment.EmployeeId)
                && assignment.Date >= from
                && assignment.Date <= to)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, WorkSchedule> schedules = await dbContext.WorkSchedules
            .AsNoTracking()
            .ToDictionaryAsync(schedule => schedule.Id, cancellationToken);

        // Who clocked in on which day. The attendance module's fact, read once
        // rather than asked per cell — and the only thing that distinguishes a
        // shift that was worked from one that was missed.
        HashSet<(Guid, DateOnly)> attended =
        [
            .. (await dbContext.AttendanceRecords
                    .AsNoTracking()
                    .Where(record => employeeIds.Contains(record.EmployeeId)
                        && record.Date >= from
                        && record.Date <= to
                        && record.ClockInAt != null)
                    .Select(record => new { record.EmployeeId, record.Date })
                    .ToListAsync(cancellationToken))
                .Select(record => (record.EmployeeId, record.Date)),
        ];

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        List<RosterRow> rows = [];

        foreach (Employee employee in people.OrderBy(
            employee => employee.LastName, StringComparer.OrdinalIgnoreCase))
        {
            List<RosterDay> days = [];

            for (DateOnly date = from; date <= to; date = date.AddDays(1))
            {
                ShiftAssignment? assignment = assignments.FirstOrDefault(row =>
                    row.EmployeeId == employee.Id && row.Date == date);

                if (assignment is null
                    || !schedules.TryGetValue(assignment.WorkScheduleId, out WorkSchedule? schedule))
                {
                    // Empty, and left empty. No invented default shift.
                    days.Add(new RosterDay { Date = date });
                    continue;
                }

                WorkingHours? hours = WorkSchedules.IsWorkingDay(schedule, date.DayOfWeek)
                    ? schedule.Days[date.DayOfWeek]
                    : null;

                days.Add(new RosterDay
                {
                    Date = date,
                    AssignmentId = assignment.Id,
                    WorkScheduleId = schedule.Id,
                    ShiftName = schedule.Name,
                    StartTime = hours?.Start,
                    EndTime = hours?.End,
                    BreakMinutes = schedule.BreakMinutes,
                    PaidHours = WorkSchedules.PaidHoursOn(schedule, date.DayOfWeek),
                    Note = assignment.Note,
                    State = Shifts.StateOf(
                        assignment, today, attended.Contains((employee.Id, date))),
                });
            }

            rows.Add(new RosterRow
            {
                EmployeeId = employee.Id,
                EmployeeName = Name(employee),
                Initials = employee.Initials,
                Department = employee.DepartmentId is Guid departmentId
                    ? departments.GetValueOrDefault(departmentId)
                    : null,
                Days = days,
            });
        }

        if (withGaps is true)
        {
            rows = [.. rows.Where(row => row.UnrosteredDays > 0)];
        }

        return new RosterResult { From = from, To = to, Rows = rows };
    }

    private async Task<ShiftResult> ReloadAsync(
        ShiftAssignment assignment,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        WorkSchedule? schedule = await dbContext.WorkSchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == assignment.WorkScheduleId, cancellationToken);

        if (schedule is null)
        {
            return ShiftResult.Failed(ShiftOutcome.NotFound);
        }

        string name = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == assignment.EmployeeId)
            .Select(employee => employee.FirstName + " " + employee.LastName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown";

        bool worked = await dbContext.AttendanceRecords
            .AsNoTracking()
            .AnyAsync(
                record => record.EmployeeId == assignment.EmployeeId
                    && record.Date == assignment.Date
                    && record.ClockInAt != null,
                cancellationToken);

        return ShiftResult.Ok(Describe(assignment, schedule, name, today, worked));
    }

    private static ShiftAssignmentDto Describe(
        ShiftAssignment assignment,
        WorkSchedule schedule,
        string employeeName,
        DateOnly today,
        bool worked)
    {
        WorkingHours? hours = WorkSchedules.IsWorkingDay(schedule, assignment.Date.DayOfWeek)
            ? schedule.Days[assignment.Date.DayOfWeek]
            : null;

        return new ShiftAssignmentDto
        {
            Id = assignment.Id,
            EmployeeId = assignment.EmployeeId,
            EmployeeName = employeeName,
            WorkScheduleId = schedule.Id,
            ShiftName = schedule.Name,
            Date = assignment.Date,
            StartTime = hours?.Start,
            EndTime = hours?.End,
            BreakMinutes = schedule.BreakMinutes,
            PaidHours = WorkSchedules.PaidHoursOn(schedule, assignment.Date.DayOfWeek),
            State = Shifts.StateOf(assignment, today, worked),
            CancelledOn = assignment.CancelledOn,
            Note = assignment.Note,
        };
    }

    private static string Name(Employee employee) =>
        string.IsNullOrWhiteSpace(employee.PreferredName)
            ? $"{employee.FirstName} {employee.LastName}".Trim()
            : $"{employee.PreferredName} {employee.LastName}".Trim();

    private Guid? ActorId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
