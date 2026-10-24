using Microsoft.EntityFrameworkCore;
using Motee.Application.Attendance;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Attendance;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Attendance;

internal sealed class AttendanceCorrectionService(
    MoteeDbContext dbContext,
    ICurrentEmployee currentEmployee,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IAttendanceCorrectionService
{
    public async Task<IReadOnlyList<AttendanceCorrectionDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        List<AttendanceCorrection> mine = await dbContext.AttendanceCorrections
            .AsNoTracking()
            .Where(correction => correction.EmployeeId == employeeId)
            .OrderByDescending(correction => correction.RequestedAt)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(mine, cancellationToken);
    }

    public async Task<CorrectionResult> RequestAsync(
        RequestCorrectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return CorrectionResult.Failed(CorrectionOutcome.EmployeeNotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return CorrectionResult.Failed(CorrectionOutcome.ReasonRequired);
        }

        DateOnly today = await TodayAsync(cancellationToken);

        if (request.Date > today)
        {
            return CorrectionResult.Failed(CorrectionOutcome.InTheFuture);
        }

        DateTimeOffset? clockIn = request.ClockInAt?.ToUniversalTime();
        DateTimeOffset? clockOut = request.ClockOutAt?.ToUniversalTime();

        if (clockIn is DateTimeOffset start
            && clockOut is DateTimeOffset end
            && end < start)
        {
            return CorrectionResult.Failed(CorrectionOutcome.OutOfOrder);
        }

        // Two open claims on one day are two answers to the same question, and
        // approving both applies whichever was read last.
        if (await dbContext.AttendanceCorrections.AnyAsync(
                candidate => candidate.EmployeeId == employeeId
                    && candidate.Date == request.Date
                    && candidate.Status == CorrectionStatus.Pending,
                cancellationToken))
        {
            return CorrectionResult.Failed(CorrectionOutcome.AlreadyPending);
        }

        AttendanceRecord? existing = await dbContext.AttendanceRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                record => record.EmployeeId == employeeId && record.Date == request.Date,
                cancellationToken);

        // Asking to change nothing. The form blocks it too, but a request that alters
        // neither time would sit in somebody's queue meaning nothing.
        if (existing?.ClockInAt == clockIn && existing?.ClockOutAt == clockOut)
        {
            return CorrectionResult.Failed(CorrectionOutcome.NothingToChange);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        AttendanceCorrection correction = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            Date = request.Date,
            RequestedClockInAt = clockIn,
            RequestedClockOutAt = clockOut,
            Reason = request.Reason.Trim(),
            Status = CorrectionStatus.Pending,
            RequestedAt = now,
        };

        dbContext.AttendanceCorrections.Add(correction);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CorrectionResult.Ok((await DescribeAsync([correction], cancellationToken))[0]);
    }

    public async Task<PagedResult<AttendanceCorrectionDto>> ListAsync(
        CorrectionQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<AttendanceCorrection> source = dbContext.AttendanceCorrections.AsNoTracking();

        if (query.EmployeeId is Guid employeeId)
        {
            source = source.Where(correction => correction.EmployeeId == employeeId);
        }

        if (query.Status is CorrectionStatus status)
        {
            source = source.Where(correction => correction.Status == status);
        }

        int total = await source.CountAsync(cancellationToken);

        // Oldest first: the queue is a queue, and somebody has been waiting.
        List<AttendanceCorrection> page = await source
            .OrderBy(correction => correction.RequestedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AttendanceCorrectionDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public Task<CorrectionResult> ApproveAsync(
        Guid id,
        string? note = null,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(id, CorrectionStatus.Approved, note, cancellationToken);

    public Task<CorrectionResult> RejectAsync(
        Guid id,
        string note,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(note)
            ? Task.FromResult(CorrectionResult.Failed(CorrectionOutcome.ReasonRequired))
            : ResolveAsync(id, CorrectionStatus.Rejected, note.Trim(), cancellationToken);

    private async Task<CorrectionResult> ResolveAsync(
        Guid id,
        CorrectionStatus status,
        string? note,
        CancellationToken cancellationToken)
    {
        AttendanceCorrection? correction = await dbContext.AttendanceCorrections
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (correction is null)
        {
            return CorrectionResult.Failed(CorrectionOutcome.NotFound);
        }

        if (!CorrectionLifecycle.Can(correction.Status))
        {
            return CorrectionResult.Failed(
                CorrectionOutcome.NotAllowed, CorrectionLifecycle.Refusal(correction.Status));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        correction.Status = status;
        correction.ResolvedAt = now;
        correction.ResolvedByUserId =
            Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;
        correction.ResolutionNote = note;

        if (status == CorrectionStatus.Approved)
        {
            await ApplyAsync(correction, now, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return CorrectionResult.Ok((await DescribeAsync([correction], cancellationToken))[0]);
    }

    // Writes the agreed times onto the day, keeping what the clock said.
    //
    // The original pair is the point. Amending in place would leave the corrected
    // time looking exactly like a real punch with nothing to say a human decided it,
    // and a disputed week needs to show the reading, the request, and who agreed.
    private async Task ApplyAsync(
        AttendanceCorrection correction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AttendanceRecord? record = await dbContext.AttendanceRecords
            .FirstOrDefaultAsync(
                candidate => candidate.EmployeeId == correction.EmployeeId
                    && candidate.Date == correction.Date,
                cancellationToken);

        if (record is null)
        {
            // Somebody who forgot entirely. The commonest correction there is, and
            // the reason this hangs off a date rather than an attendance row.
            record = new AttendanceRecord
            {
                Id = Guid.NewGuid(),
                EmployeeId = correction.EmployeeId,
                Date = correction.Date,
                CreatedAt = now,
            };

            dbContext.AttendanceRecords.Add(record);
        }
        else
        {
            correction.OriginalClockInAt = record.ClockInAt;
            correction.OriginalClockOutAt = record.ClockOutAt;
        }

        record.ClockInAt = correction.RequestedClockInAt;
        record.ClockOutAt = correction.RequestedClockOutAt;

        // Manual, and attributed to whoever approved it. A corrected day must never
        // read as a punch — this is the field anybody auditing one looks at first.
        record.Source = PunchSource.Manual;
        record.RecordedByUserId = correction.ResolvedByUserId;
        record.UpdatedAt = now;
    }

    private async Task<IReadOnlyList<AttendanceCorrectionDto>> DescribeAsync(
        IReadOnlyList<AttendanceCorrection> corrections,
        CancellationToken cancellationToken)
    {
        if (corrections.Count == 0)
        {
            return [];
        }

        List<Guid> employeeIds =
            [.. corrections.Select(correction => correction.EmployeeId).Distinct()];

        Dictionary<Guid, (string Name, string? Department)> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                Department = dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(row => row.Id, row => (row.Name, row.Department), cancellationToken);

        List<Guid> resolvers =
        [
            .. corrections
                .Where(correction => correction.ResolvedByUserId is not null)
                .Select(correction => correction.ResolvedByUserId!.Value)
                .Distinct(),
        ];

        Dictionary<Guid, string> names = resolvers.Count == 0
            ? []
            : await dbContext.Users
                .AsNoTracking()
                .Where(user => resolvers.Contains(user.Id))
                .ToDictionaryAsync(
                    user => user.Id,
                    user => user.FirstName + " " + user.LastName,
                    cancellationToken);

        return
        [
            .. corrections.Select(correction => new AttendanceCorrectionDto
            {
                Id = correction.Id,
                EmployeeId = correction.EmployeeId,
                EmployeeName = people.TryGetValue(correction.EmployeeId, out (string Name, string? Department) person)
                    ? person.Name
                    : "Unknown",
                DepartmentName = people.GetValueOrDefault(correction.EmployeeId).Department,
                Date = correction.Date,
                RequestedClockInAt = correction.RequestedClockInAt,
                RequestedClockOutAt = correction.RequestedClockOutAt,
                Reason = correction.Reason,
                Status = correction.Status,
                RequestedAt = correction.RequestedAt,
                ResolvedAt = correction.ResolvedAt,
                ResolvedByName = correction.ResolvedByUserId is Guid resolver
                    ? names.GetValueOrDefault(resolver)
                    : null,
                ResolutionNote = correction.ResolutionNote,
                OriginalClockInAt = correction.OriginalClockInAt,
                OriginalClockOutAt = correction.OriginalClockOutAt,
            }),
        ];
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
