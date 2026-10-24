using Microsoft.EntityFrameworkCore;
using Motee.Application.Attendance;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Attendance;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Attendance;

internal sealed class AttendanceService(
    MoteeDbContext dbContext,
    IWorkScheduleService schedules,
    ICurrentEmployee currentEmployee,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IAttendanceService
{
    public async Task<AttendanceDayDto?> MineAsync(
        DateOnly? date = null,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return null;
        }

        DateOnly day = date ?? await TodayAsync(cancellationToken);

        AttendanceRecord? record = await dbContext.AttendanceRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.EmployeeId == employeeId && candidate.Date == day,
                cancellationToken);

        // An empty day is still a day. Returning null would make "not started yet"
        // indistinguishable from "no such employee", and the clock widget has to
        // render something either way.
        return record is null
            ? await EmptyDayAsync(employeeId, day, cancellationToken)
            : (await DescribeAsync([record], cancellationToken)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<AttendanceDayDto>> MineBetweenAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        return await BetweenAsync(employeeId, from, to, cancellationToken);
    }

    public Task<IReadOnlyList<AttendanceDayDto>> DaysForAsync(
        Guid employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        BetweenAsync(employeeId, from, to, cancellationToken);

    public async Task<AttendanceSummaryDto> MineSummaryAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return Summarise(from, to, []);
        }

        return Summarise(from, to, await BetweenAsync(employeeId, from, to, cancellationToken));
    }

    public async Task<AttendanceSummaryDto> SummaryAsync(
        AttendanceSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<AttendanceRecord> source = dbContext.AttendanceRecords
            .AsNoTracking()
            .Where(record => record.Date >= query.From && record.Date <= query.To);

        if (query.EmployeeId is Guid employeeId)
        {
            source = source.Where(record => record.EmployeeId == employeeId);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            source = source.Where(record => dbContext.Employees
                .Any(employee => employee.Id == record.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        // Only the days that exist, unlike the per-person range. Filling every
        // calendar square for a company is one row per employee per day, and
        // "how many people were absent" is a question the rows already answer —
        // the figure the screen wants is a total, not a grid.
        List<AttendanceRecord> records = await source.ToListAsync(cancellationToken);

        return Summarise(query.From, query.To, await DescribeAsync(records, cancellationToken));
    }

    public Task<AttendanceResult> ClockInAsync(
        ClockInRequest request,
        CancellationToken cancellationToken = default) =>
        PunchAsync(ClockAction.ClockIn, cancellationToken, (record, now) =>
        {
            record.ClockInAt = now;
            record.Location = request.Location;
            record.LocationName = request.LocationName?.Trim();
            record.ClockInCoordinates = request.Coordinates?.Trim();

            // Manual is HR's label and cannot be claimed by a punch. A client asking
            // for it would otherwise make its own typing indistinguishable from
            // somebody turning up.
            record.Source = request.Source == PunchSource.Manual
                ? PunchSource.Web
                : request.Source;
        });

    public Task<AttendanceResult> StartBreakAsync(CancellationToken cancellationToken = default) =>
        PunchAsync(ClockAction.StartBreak, cancellationToken, (record, now) =>
            record.Breaks.Add(new BreakInterval { Start = now }));

    public Task<AttendanceResult> EndBreakAsync(CancellationToken cancellationToken = default) =>
        PunchAsync(ClockAction.EndBreak, cancellationToken, (record, now) =>
        {
            int open = record.Breaks.FindLastIndex(interval => interval.End is null);

            if (open >= 0)
            {
                record.Breaks[open] = record.Breaks[open] with { End = now };
            }
        });

    public Task<AttendanceResult> ClockOutAsync(
        ClockOutRequest request,
        CancellationToken cancellationToken = default) =>
        PunchAsync(ClockAction.ClockOut, cancellationToken, (record, now) =>
        {
            // Close an open break first, or somebody who went home from lunch keeps
            // accruing break time against a day that has ended.
            int open = record.Breaks.FindLastIndex(interval => interval.End is null);

            if (open >= 0)
            {
                record.Breaks[open] = record.Breaks[open] with { End = now };
            }

            record.ClockOutAt = now;
            record.ClockOutCoordinates = request.Coordinates?.Trim();
            record.Note = request.Note?.Trim();
        });

    public async Task<AttendanceResult> SetLocationAsync(
        SetWorkLocationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return AttendanceResult.Failed(AttendanceOutcome.EmployeeNotFound);
        }

        DateOnly today = await TodayAsync(cancellationToken);

        AttendanceRecord? record = await dbContext.AttendanceRecords
            .FirstOrDefaultAsync(
                candidate => candidate.EmployeeId == employeeId && candidate.Date == today,
                cancellationToken);

        if (record is null || AttendanceRecords.StateOf(record) == ClockState.NotStarted)
        {
            return AttendanceResult.Failed(
                AttendanceOutcome.NotAllowed,
                "You have not clocked in today, so there is no day to move.");
        }

        record.Location = request.Location;
        record.LocationName = request.LocationName?.Trim();
        record.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return AttendanceResult.Ok(
            (await DescribeAsync([record], cancellationToken))[0]);
    }

    public async Task<PagedResult<AttendanceDayDto>> ListAsync(
        AttendanceQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly day = query.Date ?? await TodayAsync(cancellationToken);

        IQueryable<AttendanceRecord> source = dbContext.AttendanceRecords
            .AsNoTracking()
            .Where(record => record.Date == day);

        if (query.EmployeeId is Guid employeeId)
        {
            source = source.Where(record => record.EmployeeId == employeeId);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            source = source.Where(record => dbContext.Employees
                .Any(employee => employee.Id == record.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        int total = await source.CountAsync(cancellationToken);

        List<AttendanceRecord> page = await source
            .OrderBy(record => record.ClockInAt ?? DateTimeOffset.MaxValue)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        IReadOnlyList<AttendanceDayDto> described = await DescribeAsync(page, cancellationToken);

        // Status is derived, so it cannot be filtered in SQL. Applied to the page
        // after describing it, which narrows what is shown without pretending the
        // total changed — the alternative is loading every row to count them.
        if (query.Status is AttendanceStatus status)
        {
            described = [.. described.Where(row => row.Status == status)];
        }

        return new PagedResult<AttendanceDayDto>
        {
            Items = described,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<AttendanceResult> LogAsync(
        LogAttendanceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Employees.AnyAsync(
                employee => employee.Id == request.EmployeeId, cancellationToken))
        {
            return AttendanceResult.Failed(AttendanceOutcome.EmployeeNotFound);
        }

        DateOnly today = await TodayAsync(cancellationToken);

        if (request.Date > today)
        {
            return AttendanceResult.Failed(AttendanceOutcome.InTheFuture);
        }

        // Normalised here, at the edge. A client sends the offset it is in —
        // "08:00:00+01:00" from Lagos — and the column is timestamptz, which Npgsql
        // will only write at offset zero. Passing it through unchanged is a 500 on
        // every manual entry from a client outside UTC, which is all of them in one
        // of the two countries we serve.
        //
        // The instant is unchanged by this: only how it is expressed.
        DateTimeOffset? clockIn = request.ClockInAt?.ToUniversalTime();
        DateTimeOffset? clockOut = request.ClockOutAt?.ToUniversalTime();

        if (clockIn is DateTimeOffset start
            && clockOut is DateTimeOffset end
            && end < start)
        {
            return AttendanceResult.Failed(AttendanceOutcome.OutOfOrder);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        AttendanceRecord? record = await dbContext.AttendanceRecords
            .FirstOrDefaultAsync(
                candidate => candidate.EmployeeId == request.EmployeeId
                    && candidate.Date == request.Date,
                cancellationToken);

        if (record is null)
        {
            record = new AttendanceRecord
            {
                Id = Guid.NewGuid(),
                EmployeeId = request.EmployeeId,
                Date = request.Date,
                CreatedAt = now,
            };

            dbContext.AttendanceRecords.Add(record);
        }

        record.ClockInAt = clockIn;
        record.ClockOutAt = clockOut;

        // A single synthetic break rather than instants, because HR is typing "they
        // took an hour", not when. Anchored to the clock-in so the arithmetic that
        // reads instants still works on it.
        record.Breaks = request.BreakMinutes > 0 && clockIn is DateTimeOffset from
            ?
            [
                new BreakInterval
                {
                    Start = from,
                    End = from.AddMinutes(request.BreakMinutes),
                },
            ]
            : [];

        record.Location = request.Location;
        record.LocationName = request.LocationName?.Trim();
        record.Note = request.Note?.Trim();

        // Always, and never from the request. A typed day that could pass as a punch
        // is the one thing this record must not allow.
        record.Source = PunchSource.Manual;
        record.RecordedByUserId =
            Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;
        record.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return AttendanceResult.Ok((await DescribeAsync([record], cancellationToken))[0]);
    }

    // Every clock move, which differ only in what they write and what they are allowed
    // from. Shared so the day lookup, the transition check and the save are written
    // once — three places to forget the lifecycle is how a double clock-in gets in.
    private async Task<AttendanceResult> PunchAsync(
        ClockAction action,
        CancellationToken cancellationToken,
        Action<AttendanceRecord, DateTimeOffset> apply)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return AttendanceResult.Failed(AttendanceOutcome.EmployeeNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateOnly today = await TodayAsync(cancellationToken);

        AttendanceRecord? record = await dbContext.AttendanceRecords
            .FirstOrDefaultAsync(
                candidate => candidate.EmployeeId == employeeId && candidate.Date == today,
                cancellationToken);

        if (record is null)
        {
            record = new AttendanceRecord
            {
                Id = Guid.NewGuid(),
                EmployeeId = employeeId,
                Date = today,
                Source = PunchSource.Web,
                CreatedAt = now,
            };

            dbContext.AttendanceRecords.Add(record);
        }

        ClockState state = AttendanceRecords.StateOf(record);

        if (!ClockLifecycle.Can(state, action))
        {
            return AttendanceResult.Failed(
                AttendanceOutcome.NotAllowed, ClockLifecycle.Refusal(state, action));
        }

        apply(record, now);
        record.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return AttendanceResult.Ok((await DescribeAsync([record], cancellationToken))[0]);
    }

    // Every day in the range, punched or not.
    //
    // The stored rows are filled out with empty days for the dates that have none,
    // because a calendar with gaps where somebody was absent is a calendar that hides
    // absences — and the screen cannot tell "no row" from "not loaded" without asking
    // again per square. The empty ones carry a status like any other: absent once the
    // day is over, not-clocked-in while it is running, nothing at all at the weekend.
    private async Task<IReadOnlyList<AttendanceDayDto>> BetweenAsync(
        Guid employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        if (to < from)
        {
            return [];
        }

        List<AttendanceRecord> stored = await dbContext.AttendanceRecords
            .AsNoTracking()
            .Where(record => record.EmployeeId == employeeId
                && record.Date >= from
                && record.Date <= to)
            .ToListAsync(cancellationToken);

        Dictionary<DateOnly, AttendanceRecord> byDate =
            stored.ToDictionary(record => record.Date);

        List<AttendanceRecord> every = [];

        for (DateOnly day = from; day <= to; day = day.AddDays(1))
        {
            every.Add(byDate.TryGetValue(day, out AttendanceRecord? record)
                ? record
                : new AttendanceRecord
                {
                    // Empty id marks a day that was never recorded, so a client can
                    // tell a blank square from one somebody cleared.
                    Id = Guid.Empty,
                    EmployeeId = employeeId,
                    Date = day,
                    Source = PunchSource.Web,
                });
        }

        return await DescribeAsync(every, cancellationToken);
    }

    private static AttendanceSummaryDto Summarise(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<AttendanceDayDto> days)
    {
        // Days actually attended. Leave and the weekend are neither punctual nor
        // late, and counting them would dilute the rate towards meaninglessness.
        List<AttendanceDayDto> attended =
        [
            .. days.Where(day => day.Status is AttendanceStatus.Present
                or AttendanceStatus.Late
                or AttendanceStatus.EarlyDeparture),
        ];

        int late = attended.Count(day => day.Status == AttendanceStatus.Late);

        return new AttendanceSummaryDto
        {
            From = from,
            To = to,
            TotalHours = Math.Round(days.Sum(day => day.WorkedHours), 2),
            OvertimeHours = Math.Round(days.Sum(day => day.OvertimeHours), 2),
            DaysPresent = attended.Count,
            DaysLate = late,
            DaysAbsent = days.Count(day => day.Status == AttendanceStatus.Absent),
            DaysOnLeave = days.Count(day => day.Status == AttendanceStatus.OnLeave),

            // 100 when nothing counts. A new joiner has not been late, and nought per
            // cent punctual on their first screen is a number somebody has to explain.
            PunctualityRate = attended.Count == 0
                ? 100
                : (int)Math.Round((attended.Count - late) * 100.0 / attended.Count),
        };
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        // The company's calendar, not the server's. A punch at half past midnight in
        // Lagos belongs to the day it is there.
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    // Two things this has to get right, and the first version got both wrong.
    //
    // Scoped by id, because tenants are not themselves tenant-scoped — the global
    // filter does not touch this table, so an unfiltered FirstOrDefault returns some
    // arbitrary company's row. On a server with one tenant that is invisible; on a
    // real one it judges a Lagos office against London's clock.
    //
    // And the whole Settings object, not a property of it. Settings is jsonb behind a
    // value converter, so it is opaque to the query translator — selecting a field of
    // it does not reach the column and comes back empty, which falls back to UTC and
    // quietly shifts every status by an hour.
    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        return settings?.TimeZoneId;
    }

    // A day with no punches, so the clock widget has something to render and "not
    // started" is distinguishable from "no such person".
    private async Task<AttendanceDayDto?> EmptyDayAsync(
        Guid employeeId,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        bool exists = await dbContext.Employees
            .AnyAsync(employee => employee.Id == employeeId, cancellationToken);

        if (!exists)
        {
            return null;
        }

        return (await DescribeAsync(
            [
                new AttendanceRecord
                {
                    Id = Guid.Empty,
                    EmployeeId = employeeId,
                    Date = day,
                    Source = PunchSource.Web,
                },
            ],
            cancellationToken))[0];
    }

    // Everything derived, computed here rather than stored.
    //
    // Batched: schedules, names and approved leave are looked up once for the page
    // instead of per row, which is the difference between one query and three hundred
    // on the overview screen.
    private async Task<IReadOnlyList<AttendanceDayDto>> DescribeAsync(
        IReadOnlyList<AttendanceRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return [];
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string? zone = await ZoneAsync(cancellationToken);
        DateOnly today = TenantClock.DayOf(now, zone);

        List<Guid> employeeIds = [.. records.Select(record => record.EmployeeId).Distinct()];

        Dictionary<Guid, EmployeeNames> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new EmployeeNames
            {
                Id = employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                JobTitle = employee.JobTitle,
                DepartmentName = dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(row => row.Id, cancellationToken);

        // Approved leave covering any of these days. The distinction that stops a
        // booked day off being reported as an absence to answer for.
        List<LeaveSpan> leave = await dbContext.LeaveRequests
            .AsNoTracking()
            .Where(request => employeeIds.Contains(request.EmployeeId)
                && request.Status == LeaveRequestStatus.Approved)
            .Select(request => new LeaveSpan
            {
                EmployeeId = request.EmployeeId,
                Start = request.StartDate,
                End = request.EndDate,
            })
            .ToListAsync(cancellationToken);

        // Resolved per person, not per row. A month of one employee's days is
        // thirty-one records and one schedule; resolving inside the loop made it
        // thirty-one lookups of the same answer, which is invisible on the single day
        // this was first written for and is the whole cost of the calendar screen.
        Dictionary<Guid, WorkScheduleDto?> byEmployee = [];

        foreach (Guid employeeId in employeeIds)
        {
            byEmployee[employeeId] =
                await schedules.EffectiveForAsync(employeeId, cancellationToken);
        }

        return
        [
            .. records.Select(record => Describe(
                record,
                byEmployee.GetValueOrDefault(record.EmployeeId),
                people,
                leave,
                zone,
                today,
                now)),
        ];
    }

    private static AttendanceDayDto Describe(
        AttendanceRecord record,
        WorkScheduleDto? schedule,
        Dictionary<Guid, EmployeeNames> people,
        List<LeaveSpan> leave,
        string? zone,
        DateOnly today,
        DateTimeOffset now)
    {
        EmployeeNames? person = people.GetValueOrDefault(record.EmployeeId);

        int breakMinutes = AttendanceRecords.BreakMinutes(record, now);
        decimal worked = AttendanceRecords.WorkedHours(record, now);

        string dayKey = record.Date.DayOfWeek.ToString().ToLowerInvariant();

        WorkingHoursDto? hours = schedule is not null
            && schedule.Days.TryGetValue(dayKey, out WorkingHoursDto? found)
                ? found
                : null;

        decimal scheduled = hours is null || schedule is null
            ? 0m
            : Domain.Attendance.WorkSchedules.PaidHours(
                new WorkingHours(hours.Start, hours.End), schedule.BreakMinutes);

        bool onLeave = leave.Any(span => span.EmployeeId == record.EmployeeId
            && span.Start <= record.Date
            && span.End >= record.Date);

        AttendanceStatus status = Status(record, schedule, onLeave, zone, today);

        return new AttendanceDayDto
        {
            Id = record.Id,
            EmployeeId = record.EmployeeId,
            EmployeeName = person?.Name ?? "Unknown",
            JobTitle = person?.JobTitle,
            DepartmentName = person?.DepartmentName,
            Date = record.Date,
            ClockInAt = record.ClockInAt,
            ClockOutAt = record.ClockOutAt,
            Breaks =
            [
                .. record.Breaks.Select(interval => new BreakIntervalDto
                {
                    Start = interval.Start,
                    End = interval.End,
                }),
            ],
            BreakMinutes = breakMinutes,
            State = AttendanceRecords.StateOf(record),
            Status = status,
            WorkedHours = worked,
            ScheduledHours = scheduled,
            OvertimeHours = AttendanceDay.Overtime(worked, scheduled),
            BreakShortfallMinutes = AttendanceDay.BreakShortfall(
                breakMinutes, schedule?.BreakMinutes ?? 0),
            Location = record.Location,
            LocationName = record.LocationName,
            ClockInCoordinates = record.ClockInCoordinates,
            ClockOutCoordinates = record.ClockOutCoordinates,
            Source = record.Source,
            Note = record.Note,
            RecordedByHand = record.RecordedByUserId is not null,
            ScheduleName = schedule?.Name,
        };
    }

    private static AttendanceStatus Status(
        AttendanceRecord record,
        WorkScheduleDto? schedule,
        bool onLeave,
        string? zone,
        DateOnly today)
    {
        if (onLeave)
        {
            return AttendanceStatus.OnLeave;
        }

        // Without working hours nothing can be judged. Present when they punched,
        // not-started when they did not — never absent, which is an accusation this
        // has no basis to make.
        if (schedule is null)
        {
            return record.ClockInAt is null
                ? AttendanceStatus.NotClockedIn
                : AttendanceStatus.Present;
        }

        return AttendanceDay.Status(
            ToDomain(schedule),
            record.Date.DayOfWeek,
            onApprovedLeave: false,
            arrived: record.ClockInAt is DateTimeOffset start
                ? TenantClock.TimeOf(start, zone)
                : null,
            left: record.ClockOutAt is DateTimeOffset end
                ? TenantClock.TimeOf(end, zone)
                : null,

            // A day only becomes an absence once it is over. Today is still running,
            // and somebody who starts at ten is not absent at nine.
            dayIsOver: record.Date < today);
    }

    private static WorkSchedule ToDomain(WorkScheduleDto schedule) => new()
    {
        Id = schedule.Id,
        Name = schedule.Name,
        Days = schedule.Days.ToDictionary(
            day => Enum.Parse<DayOfWeek>(day.Key, ignoreCase: true),
            day => new WorkingHours(day.Value.Start, day.Value.End)),
        BreakMinutes = schedule.BreakMinutes,
        GraceMinutes = schedule.GraceMinutes,
    };

    private sealed record EmployeeNames
    {
        public required Guid Id { get; init; }

        public required string Name { get; init; }

        public string? JobTitle { get; init; }

        public string? DepartmentName { get; init; }
    }

    private sealed record LeaveSpan
    {
        public required Guid EmployeeId { get; init; }

        public required DateOnly Start { get; init; }

        public required DateOnly End { get; init; }
    }
}
