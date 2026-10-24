using Microsoft.EntityFrameworkCore;
using Motee.Application.Attendance;
using Motee.Domain.Attendance;
using Motee.Domain.Employees;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Attendance;

internal sealed class WorkScheduleService(
    MoteeDbContext dbContext,
    TimeProvider timeProvider) : IWorkScheduleService
{
    public async Task<IReadOnlyList<WorkScheduleDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        List<WorkSchedule> schedules = await dbContext.WorkSchedules
            .AsNoTracking()
            // Default first: it is the one most people are on and the one a company
            // edits most, so it should not be alphabetised into the middle.
            .OrderByDescending(schedule => schedule.IsDefault)
            .ThenBy(schedule => schedule.Name)
            .ToListAsync(cancellationToken);

        return await WithCountsAsync(schedules, cancellationToken);
    }

    public async Task<WorkScheduleDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        WorkSchedule? schedule = await dbContext.WorkSchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return schedule is null
            ? null
            : (await WithCountsAsync([schedule], cancellationToken))[0];
    }

    public async Task<WorkScheduleDto?> EffectiveForAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        Guid? assigned = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => employee.WorkScheduleId)
            .FirstOrDefaultAsync(cancellationToken);

        // Their own, if it is still active. A deactivated schedule falls through to
        // the default rather than leaving somebody judged against hours the company
        // has retired — which would be invisible, because the row still reads fine.
        if (assigned is Guid scheduleId)
        {
            WorkSchedule? own = await dbContext.WorkSchedules
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    schedule => schedule.Id == scheduleId && schedule.IsActive,
                    cancellationToken);

            if (own is not null)
            {
                return Describe(own, assignedCount: 0);
            }
        }

        WorkSchedule? fallback = await dbContext.WorkSchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(schedule => schedule.IsDefault, cancellationToken);

        // AssignedCount is left at zero rather than counted. This answers "which hours
        // is this person held to", and attendance asks it per employee per day — a
        // count nobody reads is not worth a query on that path.
        return fallback is null ? null : Describe(fallback, assignedCount: 0);
    }

    public async Task<WorkScheduleResult> CreateAsync(
        WorkScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Validate(request) is WorkScheduleOutcome problem)
        {
            return WorkScheduleResult.Failed(problem);
        }

        string name = request.Name.Trim();

        if (await dbContext.WorkSchedules.AnyAsync(
                schedule => schedule.Name == name, cancellationToken))
        {
            return WorkScheduleResult.Failed(WorkScheduleOutcome.DuplicateName);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        WorkSchedule created = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Days = Days(request),
            BreakMinutes = request.BreakMinutes,
            GraceMinutes = request.GraceMinutes,

            // Never by creation. The default moves deliberately, through its own call,
            // so adding a night shift cannot quietly change what every new hire is
            // held to.
            IsDefault = false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.WorkSchedules.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkScheduleResult.Ok((await GetAsync(created.Id, cancellationToken))!);
    }

    public async Task<WorkScheduleResult> UpdateAsync(
        Guid id,
        WorkScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        WorkSchedule? schedule = await dbContext.WorkSchedules
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (schedule is null)
        {
            return WorkScheduleResult.Failed(WorkScheduleOutcome.NotFound);
        }

        if (Validate(request) is WorkScheduleOutcome problem)
        {
            return WorkScheduleResult.Failed(problem);
        }

        string name = request.Name.Trim();

        if (await dbContext.WorkSchedules.AnyAsync(
                candidate => candidate.Name == name && candidate.Id != id, cancellationToken))
        {
            return WorkScheduleResult.Failed(WorkScheduleOutcome.DuplicateName);
        }

        schedule.Name = name;
        schedule.Days = Days(request);
        schedule.BreakMinutes = request.BreakMinutes;
        schedule.GraceMinutes = request.GraceMinutes;
        schedule.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkScheduleResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<WorkScheduleOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        WorkSchedule? schedule = await dbContext.WorkSchedules
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (schedule is null)
        {
            return WorkScheduleOutcome.NotFound;
        }

        // Something has to catch the next hire.
        if (schedule.IsDefault)
        {
            return WorkScheduleOutcome.IsDefault;
        }

        // Refused rather than cascading them onto the default. Moving people between
        // schedules changes what counts as late for them, and doing that as a side
        // effect of a delete is a change nobody would find afterwards.
        if (await dbContext.Employees.AnyAsync(
                employee => employee.WorkScheduleId == id, cancellationToken))
        {
            return WorkScheduleOutcome.InUse;
        }

        dbContext.WorkSchedules.Remove(schedule);
        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkScheduleOutcome.Succeeded;
    }

    public async Task<WorkScheduleOutcome> MakeDefaultAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        WorkSchedule? incoming = await dbContext.WorkSchedules
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (incoming is null)
        {
            return WorkScheduleOutcome.NotFound;
        }

        if (incoming.IsDefault)
        {
            return WorkScheduleOutcome.Succeeded;
        }

        // Cleared before it is set. A unique index allows one default per tenant, so
        // setting the new one first fails on the old — and the two writes have to be
        // one transaction either way, or a crash between them leaves a company with
        // no default and every new hire unjudgeable.
        List<WorkSchedule> current = await dbContext.WorkSchedules
            .Where(schedule => schedule.IsDefault)
            .ToListAsync(cancellationToken);

        foreach (WorkSchedule schedule in current)
        {
            schedule.IsDefault = false;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        incoming.IsDefault = true;

        // The default is what catches everybody with no choice recorded, so it cannot
        // be one the company has retired.
        incoming.IsActive = true;
        incoming.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkScheduleOutcome.Succeeded;
    }

    public async Task<WorkScheduleOutcome> AssignAsync(
        Guid employeeId,
        Guid? scheduleId,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .FirstOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);

        if (employee is null)
        {
            return WorkScheduleOutcome.EmployeeNotFound;
        }

        // Null puts them back on the company default, which is a real choice and not
        // an omission — it is how somebody comes off a night shift.
        if (scheduleId is Guid id)
        {
            if (!await dbContext.WorkSchedules.AnyAsync(
                    schedule => schedule.Id == id && schedule.IsActive, cancellationToken))
            {
                return WorkScheduleOutcome.NotFound;
            }
        }

        employee.WorkScheduleId = scheduleId;
        employee.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkScheduleOutcome.Succeeded;
    }

    private static Dictionary<DayOfWeek, WorkingHours> Days(WorkScheduleRequest request) =>
        request.Days.ToDictionary(
            day => Enum.Parse<DayOfWeek>(day.Key, ignoreCase: true),
            day => new WorkingHours(day.Value.Start, day.Value.End));

    private static WorkScheduleOutcome? Validate(WorkScheduleRequest request)
    {
        if (request.Days.Count == 0)
        {
            return WorkScheduleOutcome.NoWorkingDays;
        }

        foreach (KeyValuePair<string, WorkingHoursDto> day in request.Days)
        {
            if (!Enum.TryParse(day.Key, ignoreCase: true, out DayOfWeek _))
            {
                return WorkScheduleOutcome.InvalidHours;
            }

            // Equal is refused; later-than is a night shift and wraps. A day that
            // starts and ends at the same moment is nobody's contract.
            if (day.Value.Start == day.Value.End)
            {
                return WorkScheduleOutcome.InvalidHours;
            }

            if (WorkSchedules.PaidHours(
                    new WorkingHours(day.Value.Start, day.Value.End),
                    request.BreakMinutes) <= 0m)
            {
                return WorkScheduleOutcome.BreakLongerThanShift;
            }
        }

        return request.GraceMinutes < 0 ? WorkScheduleOutcome.InvalidHours : null;
    }

    // Mapped after materialising, not projected in SQL. Days is jsonb behind a value
    // converter and WeeklyHours is a C# method, so neither survives translation — the
    // projection compiled and would have thrown on the first call.
    //
    // Cheap to do in memory: a company has a handful of schedules, not a page of them.
    private async Task<IReadOnlyList<WorkScheduleDto>> WithCountsAsync(
        List<WorkSchedule> schedules,
        CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. schedules.Select(schedule => schedule.Id)];

        // One grouped query rather than a count per row.
        Dictionary<Guid, int> counts = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.WorkScheduleId != null
                && ids.Contains(employee.WorkScheduleId.Value))
            .GroupBy(employee => employee.WorkScheduleId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);

        return
        [
            .. schedules.Select(schedule =>
                Describe(schedule, counts.GetValueOrDefault(schedule.Id))),
        ];
    }

    private static WorkScheduleDto Describe(WorkSchedule schedule, int assignedCount) => new()
    {
        Id = schedule.Id,
        Name = schedule.Name,
        Days = schedule.Days.ToDictionary(
            day => day.Key.ToString().ToLowerInvariant(),
            day => new WorkingHoursDto { Start = day.Value.Start, End = day.Value.End },
            StringComparer.Ordinal),
        BreakMinutes = schedule.BreakMinutes,
        GraceMinutes = schedule.GraceMinutes,
        IsDefault = schedule.IsDefault,
        IsActive = schedule.IsActive,
        WeeklyHours = WorkSchedules.WeeklyHours(schedule),

        // Those explicitly on it. People on the default sit here as zero, because they
        // have made no choice — the count answers "who would I have to move before
        // deleting this", which is exactly that set.
        AssignedCount = assignedCount,
    };
}
