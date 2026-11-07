using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Sickness;
using Motee.Application.Tenancy;
using Motee.Domain.Attendance;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Sickness;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Sickness;

// Absence records, and the three things derived from them.
//
// The derivations are the reason this service is bigger than a CRUD wrapper.
// Fit-note status, the Bradford score and whether a return-to-work is overdue
// are all functions of today's date, and all three were stored on the
// frontend — which is why a three-day absence that ran to three weeks never
// appeared on the list of notes to chase.
internal sealed class SicknessService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : ISicknessService
{
    // UK self-certification: seven calendar days, a fit note after that. A
    // setting in the scheme builder the client designed; until schemes exist
    // this is the statutory default, and it is named rather than inlined so
    // there is one place to change when they do.
    private const int SelfCertDays = 7;

    // Four calendar weeks off in one stretch. The point at which an absence
    // stops being an illness to cover for and becomes a case to manage.
    private const int LongTermDays = 28;

    // A return-to-work conversation is supposed to happen on the day somebody
    // is back. Three days' grace before it is chased.
    private const int ReturnToWorkGraceDays = 3;

    // Scheme settings, until schemes exist. Named here so the two places that
    // will read them from an enrolment are already obvious.
    private const RtwInterviewRule InterviewRule = RtwInterviewRule.Always;

    private const int InterviewAfterDays = 1;

    private static readonly DayOfWeek[] WeekdayFallback =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
    ];

    private Dictionary<Guid, Person>? _people;

    private Dictionary<Guid, IReadOnlySet<DayOfWeek>>? _patterns;

    private HashSet<DateOnly>? _holidays;

    private string? _zone;

    // ── reading ─────────────────────────────────────────────────────────────

    public async Task<PagedResult<SicknessRecordDto>> ListAsync(
        SicknessQuery query,
        bool canViewMedical = false,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        IQueryable<SicknessRecord> rows = dbContext.SicknessRecords.AsNoTracking();

        if (query.EmployeeId is Guid employeeId)
        {
            rows = rows.Where(record => record.EmployeeId == employeeId);
        }

        if (query.ApprovalStatus is SicknessApprovalStatus status)
        {
            rows = rows.Where(record => record.ApprovalStatus == status);
        }

        if (query.AbsenceState is AbsenceState state)
        {
            rows = rows.Where(record => record.AbsenceState == state);
        }

        if (query.Kind is SicknessKind kind)
        {
            rows = rows.Where(record => record.Kind == kind);
        }

        if (query.ReturnToWorkPending is bool pending)
        {
            rows = pending
                ? rows.Where(record => record.RtwStatus == RtwStatus.Pending)
                : rows.Where(record => record.RtwStatus != RtwStatus.Pending);
        }

        // An absence already under way belongs in this month's list. Filtering
        // on the start date alone loses exactly the people still off.
        if (query.From is DateOnly from)
        {
            rows = rows.Where(record => (record.EndDate ?? today) >= from);
        }

        if (query.To is DateOnly to)
        {
            rows = rows.Where(record => record.StartDate <= to);
        }

        // Department and name go into SQL rather than being sieved in memory.
        // Both are joins the database can do, and doing them here is what
        // keeps the read below from being the whole table.
        if (query.DepartmentId is Guid departmentId)
        {
            rows = rows.Where(record => dbContext.Employees
                .Any(employee => employee.Id == record.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string needle = $"%{query.Search.Trim()}%";

            rows = rows.Where(record => dbContext.Employees
                .Any(employee => employee.Id == record.EmployeeId
                    && EF.Functions.ILike(
                        employee.FirstName + " " + employee.LastName, needle)));
        }

        rows = rows
            .OrderByDescending(record => record.StartDate)
            .ThenByDescending(record => record.ReportedAt);

        // Fit-note status and long-term both depend on today's date and on the
        // documents in a jsonb column, so neither can be asked of SQL. Only
        // when one is requested does this read past a page — and then it is
        // still bounded by whatever else the caller filtered on.
        bool derived = query.FitNoteOutstanding is not null || query.LongTerm is not null;

        if (!derived)
        {
            int total = await rows.CountAsync(cancellationToken);

            List<SicknessRecord> page = await rows
                .Skip(query.Skip)
                .Take(query.PageSize)
                .ToListAsync(cancellationToken);

            return new PagedResult<SicknessRecordDto>
            {
                Items = await DescribeAsync(page, today, canViewMedical, cancellationToken),
                Page = query.Page,
                PageSize = query.PageSize,
                TotalItems = total,
            };
        }

        List<SicknessRecordDto> described = await DescribeAsync(
            await rows.ToListAsync(cancellationToken), today, canViewMedical, cancellationToken);

        if (query.FitNoteOutstanding is bool chase)
        {
            described = [.. described.Where(record =>
                FitNotes.IsOutstanding(record.FitNoteStatus) == chase)];
        }

        if (query.LongTerm is bool longTerm)
        {
            described = [.. described.Where(record => record.LongTerm == longTerm)];
        }

        // Paged after the derived filters, not before: a page of twenty-five
        // taken first and then filtered down to four is a list that looks
        // empty while the next page is full.
        return new PagedResult<SicknessRecordDto>
        {
            Items = [.. described.Skip(query.Skip).Take(query.PageSize)],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = described.Count,
        };
    }

    public async Task<SicknessRecordDto?> GetAsync(
        Guid id,
        bool canViewMedical = false,
        CancellationToken cancellationToken = default)
    {
        SicknessRecord? record = await dbContext.SicknessRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (record is null)
        {
            return null;
        }

        return (await DescribeAsync(
            [record], await TodayAsync(cancellationToken), canViewMedical, cancellationToken))[0];
    }

    public async Task<SicknessSummaryDto> SummaryAsync(
        DateOnly? from = null,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        DateOnly start = from ?? DateOnly.MinValue;

        List<SicknessRecord> records = await CountingAsync(start, today, cancellationToken);

        IReadOnlyDictionary<Guid, IReadOnlySet<DayOfWeek>> patterns =
            await PatternsAsync(cancellationToken);

        IReadOnlySet<DateOnly> holidays = await HolidaysAsync(cancellationToken);

        decimal daysLost = 0m;
        decimal longDays = 0m;
        decimal shortDays = 0m;
        int longCount = 0;
        int shortCount = 0;

        foreach (SicknessRecord record in records)
        {
            decimal lost = Absences.WorkingDaysLost(
                record, today, Pattern(patterns, record.EmployeeId), holidays);

            daysLost += lost;

            if (Absences.IsLongTerm(record, today, LongTermDays))
            {
                longCount++;
                longDays += lost;
            }
            else
            {
                shortCount++;
                shortDays += lost;
            }
        }

        int people = records.Select(record => record.EmployeeId).Distinct().Count();

        int headcount = await dbContext.Employees
            .AsNoTracking()
            .CountAsync(
                employee => employee.Status == EmployeeStatus.Active
                    || employee.Status == EmployeeStatus.Probation
                    || employee.Status == EmployeeStatus.OnLeave,
                cancellationToken);

        decimal averageSpell = records.Count == 0
            ? 0m
            : Math.Round(
                (decimal)records.Sum(record => Absences.SpellLength(record, today)) / records.Count,
                1,
                MidpointRounding.AwayFromZero);

        // The denominator is everybody's working days over the period, so the
        // rate reads as "this share of available time was lost".
        decimal available = headcount * WorkingDaysBetween(
            from ?? records.Select(record => record.StartDate).DefaultIfEmpty(today).Min(),
            today,
            holidays);

        List<SicknessRecordDto> described =
            await DescribeAsync(records, today, canViewMedical: false, cancellationToken);

        return new SicknessSummaryDto
        {
            AsOf = today,
            From = from,
            Episodes = records.Count,
            People = people,
            DaysLost = Round(daysLost),
            AverageSpellDays = averageSpell,
            AbsenceRatePercent = available <= 0m
                ? 0m
                : Math.Round(daysLost / available * 100m, 1, MidpointRounding.AwayFromZero),
            Ongoing = records.Count(record => record.AbsenceState == AbsenceState.Ongoing),
            AwaitingApproval = records.Count(
                record => record.ApprovalStatus == SicknessApprovalStatus.AwaitingApproval),
            FitNotesOutstanding = described.Count(
                record => FitNotes.IsOutstanding(record.FitNoteStatus)),
            ReturnToWorkDue = records.Count(record => record.RtwStatus == RtwStatus.Pending),
            LongTerm = new CountAndDaysDto { Count = longCount, Days = Round(longDays) },
            ShortTerm = new CountAndDaysDto { Count = shortCount, Days = Round(shortDays) },
        };
    }

    public async Task<IReadOnlyList<SicknessAlertDto>> AlertsAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        List<SicknessRecord> records = await CountingAsync(
            DateOnly.MinValue, today, cancellationToken);

        List<SicknessRecordDto> described =
            await DescribeAsync(records, today, canViewMedical: false, cancellationToken);

        List<SicknessAlertDto> alerts = [];

        foreach (SicknessRecordDto record in described)
        {
            bool stillOff = record.AbsenceState == AbsenceState.Ongoing;

            if (FitNotes.IsOutstanding(record.FitNoteStatus))
            {
                alerts.Add(Alert(
                    record,
                    SicknessAlertKind.FitNoteOutstanding,
                    record.FitNoteStatus == FitNoteStatus.Expired
                        ? $"{record.EmployeeName} — fit note expired, {record.SpellDays} days off"
                        : $"{record.EmployeeName} — fit note outstanding after {record.SpellDays} days",

                    // Still off and still unevidenced is the one to ring today.
                    urgent: stillOff));
            }

            if (record.ReturnToWork.Overdue)
            {
                alerts.Add(Alert(
                    record,
                    SicknessAlertKind.ReturnToWorkOverdue,
                    $"{record.EmployeeName} — return-to-work not completed",
                    urgent: false));
            }

            if (record.LongTerm && stillOff)
            {
                alerts.Add(Alert(
                    record,
                    SicknessAlertKind.LongTermAbsence,
                    $"{record.EmployeeName} — off {record.SpellDays} days and counting",
                    urgent: true));
            }

            if (record.ApprovalStatus == SicknessApprovalStatus.AwaitingApproval)
            {
                alerts.Add(Alert(
                    record,
                    SicknessAlertKind.AwaitingApproval,
                    $"{record.EmployeeName} — absence waiting for a decision",
                    urgent: false));
            }
        }

        return [.. alerts
            .OrderByDescending(alert => alert.Urgent)
            .ThenBy(alert => alert.StartDate)];
    }

    private static SicknessAlertDto Alert(
        SicknessRecordDto record,
        SicknessAlertKind kind,
        string message,
        bool urgent) => new()
        {
            RecordId = record.Id,
            EmployeeId = record.EmployeeId,
            EmployeeName = record.EmployeeName,
            Kind = kind,
            Message = message,
            Urgent = urgent,
            StartDate = record.StartDate,
        };

    // ── writing ─────────────────────────────────────────────────────────────

    public async Task<SicknessResult> ReportAsync(
        SicknessRequest request,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        if (request.EndDate is DateOnly end && end < request.StartDate)
        {
            return SicknessResult.Failed(SicknessOutcome.EndBeforeStart);
        }

        if (!await dbContext.Employees.AnyAsync(
            employee => employee.Id == request.EmployeeId
                && employee.Status != EmployeeStatus.Deleted,
            cancellationToken))
        {
            return SicknessResult.Failed(SicknessOutcome.EmployeeNotFound);
        }

        if (await OverlapsAsync(request.EmployeeId, request, null, today, cancellationToken))
        {
            return SicknessResult.Failed(SicknessOutcome.OverlapsAnother);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        Guid? actor = await currentEmployee.IdAsync(cancellationToken);

        SicknessRecord record = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            EmployeeId = request.EmployeeId,
            ReportedByEmployeeId = actor,
            ReportedByName = actor is Guid who ? NameOf(await PeopleAsync(cancellationToken), who) : null,
            ReportedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apply(record, request, now);
        OpenReturnToWork(record, today);

        dbContext.SicknessRecords.Add(record);

        await dbContext.SaveChangesAsync(cancellationToken);

        return await OkAsync(record, today, cancellationToken);
    }

    public async Task<SicknessResult> UpdateAsync(
        Guid id,
        SicknessRequest request,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        SicknessRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return SicknessResult.Failed(SicknessOutcome.NotFound);
        }

        if (request.EndDate is DateOnly end && end < request.StartDate)
        {
            return SicknessResult.Failed(SicknessOutcome.EndBeforeStart);
        }

        if (await OverlapsAsync(record.EmployeeId, request, id, today, cancellationToken))
        {
            return SicknessResult.Failed(SicknessOutcome.OverlapsAnother);
        }

        Apply(record, request, timeProvider.GetUtcNow());

        if (record.RtwStatus == RtwStatus.NotDue)
        {
            OpenReturnToWork(record, today);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await OkAsync(record, today, cancellationToken);
    }

    public async Task<SicknessResult> ApproveAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        await DecideAsync(id, SicknessApprovalStatus.Approved, null, cancellationToken);

    public async Task<SicknessResult> RejectAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(reason)
            ? SicknessResult.Failed(SicknessOutcome.ReasonRequired)
            : await DecideAsync(id, SicknessApprovalStatus.Rejected, reason, cancellationToken);

    public async Task<SicknessResult> CancelAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(reason)
            ? SicknessResult.Failed(SicknessOutcome.ReasonRequired)
            : await DecideAsync(id, SicknessApprovalStatus.Cancelled, reason, cancellationToken);

    private async Task<SicknessResult> DecideAsync(
        Guid id,
        SicknessApprovalStatus decision,
        string? reason,
        CancellationToken cancellationToken)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        SicknessRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return SicknessResult.Failed(SicknessOutcome.NotFound);
        }

        SicknessRefusal refusal = decision switch
        {
            SicknessApprovalStatus.Approved => SicknessLifecycle.CanApprove(record),
            SicknessApprovalStatus.Rejected => SicknessLifecycle.CanReject(record),
            _ => SicknessLifecycle.CanCancel(record),
        };

        if (refusal != SicknessRefusal.None)
        {
            return SicknessResult.Refused(refusal);
        }

        record.ApprovalStatus = decision;
        record.UpdatedAt = timeProvider.GetUtcNow();

        if (reason is not null)
        {
            record.RtwNotes = string.IsNullOrWhiteSpace(record.RtwNotes)
                ? reason.Trim()
                : $"{record.RtwNotes}\n{reason.Trim()}";
        }

        // Nobody owes a return-to-work for an absence that did not happen.
        if (decision is SicknessApprovalStatus.Rejected or SicknessApprovalStatus.Cancelled)
        {
            record.RtwStatus = RtwStatus.NotDue;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await OkAsync(record, today, cancellationToken);
    }

    public async Task<SicknessResult> ReturnAsync(
        Guid id,
        ReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        SicknessRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return SicknessResult.Failed(SicknessOutcome.NotFound);
        }

        SicknessRefusal refusal = SicknessLifecycle.CanReturn(
            record, request.LastSickDay, request.ReturnDate);

        if (refusal != SicknessRefusal.None)
        {
            return SicknessResult.Refused(refusal);
        }

        record.EndDate = request.LastSickDay;
        record.EndPortion = request.LastDayPortion;
        record.AbsenceState = AbsenceState.Returned;
        record.RtwReturnDate = request.ReturnDate;
        record.UpdatedAt = timeProvider.GetUtcNow();

        OpenReturnToWork(record, today);

        await dbContext.SaveChangesAsync(cancellationToken);

        return await OkAsync(record, today, cancellationToken);
    }

    public async Task<SicknessResult> CompleteReturnToWorkAsync(
        Guid id,
        ReturnToWorkRequest request,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        SicknessRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return SicknessResult.Failed(SicknessOutcome.NotFound);
        }

        SicknessRefusal refusal = SicknessLifecycle.CanCompleteReturnToWork(
            record, request.InterviewHeld, request.SafeToReturn);

        if (refusal != SicknessRefusal.None)
        {
            return SicknessResult.Refused(refusal);
        }

        record.RtwStatus = RtwStatus.Completed;
        record.RtwInterviewHeld = request.InterviewHeld;
        record.RtwInterviewDate = request.InterviewDate;
        record.RtwInterviewerEmployeeId = request.InterviewerEmployeeId;
        record.RtwSafeToReturn = request.SafeToReturn;
        record.RtwNotes = string.IsNullOrWhiteSpace(request.Notes) ? record.RtwNotes : request.Notes.Trim();
        record.RtwAdjustmentsRecommended = request.AdjustmentsRecommended;
        record.RtwOhReferralRecommended = request.OhReferralRecommended;
        record.RtwCompletedByEmployeeId = await currentEmployee.IdAsync(cancellationToken);
        record.RtwCompletedAt = timeProvider.GetUtcNow();
        record.UpdatedAt = record.RtwCompletedAt.Value;

        await dbContext.SaveChangesAsync(cancellationToken);

        return await OkAsync(record, today, cancellationToken);
    }

    public async Task<SicknessResult> AddDocumentAsync(
        Guid id,
        SicknessDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        SicknessRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return SicknessResult.Failed(SicknessOutcome.NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return SicknessResult.Failed(SicknessOutcome.DocumentNameRequired);
        }

        // Replaced rather than mutated: the jsonb converter compares by
        // serialising, and an in-place Add on the existing list is not seen as
        // a change.
        record.Documents =
        [
            .. record.Documents,
            new SicknessDocument
            {
                Id = Guid.NewGuid(),
                Kind = request.Kind,
                Name = request.Name.Trim(),
                FileId = request.FileId,
                ValidFrom = request.ValidFrom,
                ValidTo = request.ValidTo,
                UploadedAt = timeProvider.GetUtcNow(),
                UploadedByEmployeeId = await currentEmployee.IdAsync(cancellationToken),
            },
        ];

        record.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return await OkAsync(record, today, cancellationToken);
    }

    public async Task<SicknessResult> ExcludeAsync(
        Guid id,
        ExclusionRequest request,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        SicknessRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return SicknessResult.Failed(SicknessOutcome.NotFound);
        }

        // A reason is the whole point. An exclusion with no stated ground is
        // indistinguishable from a score being quietly adjusted.
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return SicknessResult.Failed(SicknessOutcome.ReasonRequired);
        }

        record.ExclusionCategory = request.Category;
        record.ExclusionReason = request.Reason.Trim();
        record.ExclusionApprovedByEmployeeId = await currentEmployee.IdAsync(cancellationToken);
        record.ExclusionApprovedAt = timeProvider.GetUtcNow();
        record.UpdatedAt = record.ExclusionApprovedAt.Value;

        await dbContext.SaveChangesAsync(cancellationToken);

        return await OkAsync(record, today, cancellationToken);
    }

    public async Task<SicknessResult> ClearExclusionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        SicknessRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return SicknessResult.Failed(SicknessOutcome.NotFound);
        }

        record.ExclusionCategory = null;
        record.ExclusionReason = null;
        record.ExclusionApprovedByEmployeeId = null;
        record.ExclusionApprovedAt = null;
        record.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return await OkAsync(record, today, cancellationToken);
    }

    // ── Bradford ────────────────────────────────────────────────────────────

    public async Task<BradfordDto> BradfordAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        BradfordSettings settings = BradfordSettings.Default;

        List<SicknessRecord> records = await dbContext.SicknessRecords
            .AsNoTracking()
            .Where(record => record.EmployeeId == employeeId
                && record.StartDate >= today.AddDays(-((settings.WindowWeeks * 7) + 400)))
            .ToListAsync(cancellationToken);

        BradfordResult result = BradfordFactor.Calculate(
            employeeId,
            records,
            today,
            settings,
            Pattern(await PatternsAsync(cancellationToken), employeeId),
            await HolidaysAsync(cancellationToken));

        return new BradfordDto
        {
            EmployeeId = employeeId,
            EmployeeName = NameOf(await PeopleAsync(cancellationToken), employeeId) ?? "Unknown",
            WindowStart = result.WindowStart,
            WindowEnd = result.WindowEnd,
            Spells = result.Spells,
            Days = result.Days,
            Score = result.Score,
            Action = result.Action,
            Band = result.Band,
            Episodes =
            [
                .. result.Episodes.Select(episode => new BradfordEpisodeDto
                {
                    Start = episode.Start,
                    End = episode.End,
                    Days = episode.Days,
                    Clipped = episode.Clipped,
                }),
            ],
            Excluded =
            [
                .. result.Excluded.Select(excluded => new BradfordExclusionDto
                {
                    RecordId = excluded.RecordId,
                    Reason = excluded.Reason,
                }),
            ],
        };
    }

    public async Task<IReadOnlyList<BradfordRowDto>> BradfordBoardAsync(
        BradfordBoardQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        BradfordSettings settings = BradfordSettings.Default;
        DateOnly earliest = today.AddDays(-((settings.WindowWeeks * 7) + 400));

        List<SicknessRecord> records = await dbContext.SicknessRecords
            .AsNoTracking()
            .Where(record => record.StartDate >= earliest)
            .ToListAsync(cancellationToken);

        IReadOnlyDictionary<Guid, Person> people = await PeopleAsync(cancellationToken);
        IReadOnlyDictionary<Guid, IReadOnlySet<DayOfWeek>> patterns = await PatternsAsync(cancellationToken);
        IReadOnlySet<DateOnly> holidays = await HolidaysAsync(cancellationToken);

        List<BradfordRowDto> board = [];

        // Only people with absence in the window. Everybody else scores zero,
        // and a board full of zeroes buries the handful of rows it exists to
        // surface.
        foreach (Guid employeeId in records.Select(record => record.EmployeeId).Distinct())
        {
            if (!people.TryGetValue(employeeId, out Person? person))
            {
                continue;
            }

            if (query.DepartmentId is Guid departmentId && person.DepartmentId != departmentId)
            {
                continue;
            }

            BradfordResult result = BradfordFactor.Calculate(
                employeeId, records, today, settings, Pattern(patterns, employeeId), holidays);

            if (result.Score <= 0m)
            {
                continue;
            }

            if (query.AtLeast is BradfordAction least && result.Action < least)
            {
                continue;
            }

            board.Add(new BradfordRowDto
            {
                EmployeeId = employeeId,
                EmployeeName = person.Name,
                DepartmentName = person.DepartmentName,
                Spells = result.Spells,
                Days = result.Days,
                Score = result.Score,
                Action = result.Action,
                Band = result.Band,
            });
        }

        return
        [
            .. board
                .OrderByDescending(row => row.Score)
                .ThenBy(row => row.EmployeeName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(query.Limit, 1, 500)),
        ];
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<SicknessRecord?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.SicknessRecords.FirstOrDefaultAsync(record => record.Id == id, cancellationToken);

    private async Task<SicknessResult> OkAsync(
        SicknessRecord record,
        DateOnly today,
        CancellationToken cancellationToken) =>
        SicknessResult.Ok(
            (await DescribeAsync([record], today, canViewMedical: true, cancellationToken))[0]);

    private static void Apply(SicknessRecord record, SicknessRequest request, DateTimeOffset now)
    {
        record.EmployeeId = request.EmployeeId;
        record.Kind = request.Kind;
        record.StartDate = request.StartDate;
        record.StartPortion = request.StartPortion;
        record.LeftWorkAt = request.LeftWorkAt;
        record.EndDate = request.EndDate;
        record.EndPortion = request.EndPortion;
        record.OvernightShift = request.OvernightShift;
        record.ExpectedReturnDate = request.ExpectedReturnDate;
        record.Reason = request.Reason;
        record.ReasonDetail = string.IsNullOrWhiteSpace(request.ReasonDetail)
            ? null
            : request.ReasonDetail.Trim();
        record.Hospitalised = request.Hospitalised;
        record.NotifiedAt = request.NotifiedAt ?? now;
        record.Source = request.Source;
        record.LinkedRecordId = request.LinkedRecordId;
        record.AbsenceState = request.EndDate is null ? AbsenceState.Ongoing : AbsenceState.Returned;
        record.UpdatedAt = now;
    }

    // A return-to-work is owed the moment somebody is back, and whether it
    // needs an interview depends on how long they were off.
    //
    // The rule belongs to the sick-pay scheme, which is not built yet. Until
    // it is, every absence gets an interview — matching the default scheme
    // the client specified, and erring towards the conversation happening.
    // When schemes land, these two values come from the enrolment rather than
    // from here.
    private static void OpenReturnToWork(SicknessRecord record, DateOnly today)
    {
        if (record.AbsenceState != AbsenceState.Returned
            || !SicknessLifecycle.Counts(record.ApprovalStatus))
        {
            return;
        }

        record.RtwStatus = RtwStatus.Pending;
        record.RtwLastSickDay = record.EndDate;
        record.RtwInterviewRequired = SicknessLifecycle.InterviewRequired(
            InterviewRule, Absences.SpellLength(record, today), InterviewAfterDays);
    }

    // The same person off over the same days twice. Checked against what
    // counts, so a cancelled record does not block the corrected one that
    // replaces it.
    private async Task<bool> OverlapsAsync(
        Guid employeeId,
        SicknessRequest request,
        Guid? ignoring,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        DateOnly start = request.StartDate;
        DateOnly end = request.EndDate ?? today;

        List<SicknessRecord> theirs = await dbContext.SicknessRecords
            .AsNoTracking()
            .Where(record => record.EmployeeId == employeeId
                && (record.ApprovalStatus == SicknessApprovalStatus.AwaitingApproval
                    || record.ApprovalStatus == SicknessApprovalStatus.Approved))
            .ToListAsync(cancellationToken);

        return theirs.Exists(record =>
            record.Id != ignoring
            && record.StartDate <= end
            && (record.EndDate ?? today) >= start);
    }

    private async Task<List<SicknessRecord>> CountingAsync(
        DateOnly from,
        DateOnly today,
        CancellationToken cancellationToken) =>
        await dbContext.SicknessRecords
            .AsNoTracking()
            .Where(record =>
                (record.ApprovalStatus == SicknessApprovalStatus.AwaitingApproval
                    || record.ApprovalStatus == SicknessApprovalStatus.Approved)
                && (record.EndDate ?? today) >= from)
            .ToListAsync(cancellationToken);

    private async Task<List<SicknessRecordDto>> DescribeAsync(
        IReadOnlyList<SicknessRecord> records,
        DateOnly today,
        bool canViewMedical,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<Guid, Person> people = await PeopleAsync(cancellationToken);
        IReadOnlyDictionary<Guid, IReadOnlySet<DayOfWeek>> patterns = await PatternsAsync(cancellationToken);
        IReadOnlySet<DateOnly> holidays = await HolidaysAsync(cancellationToken);

        return [.. records.Select(record =>
        {
            Person? person = people.GetValueOrDefault(record.EmployeeId);
            int spell = Absences.SpellLength(record, today);

            return new SicknessRecordDto
            {
                Id = record.Id,
                EmployeeId = record.EmployeeId,
                EmployeeName = person?.Name ?? "Unknown",
                DepartmentName = person?.DepartmentName,
                ManagerId = person?.ManagerId,
                Kind = record.Kind,
                StartDate = record.StartDate,
                StartPortion = record.StartPortion,
                LeftWorkAt = record.LeftWorkAt,
                EndDate = record.EndDate,
                EndPortion = record.EndPortion,
                OvernightShift = record.OvernightShift,
                ExpectedReturnDate = record.ExpectedReturnDate,

                // Withheld rather than softened. A redacted category that
                // looks like a recorded one would be counted by a report.
                Reason = canViewMedical ? record.Reason : null,
                ReasonDetail = canViewMedical ? record.ReasonDetail : null,
                MedicalDetailWithheld = !canViewMedical,

                Hospitalised = record.Hospitalised,
                NotifiedAt = record.NotifiedAt,
                Source = record.Source,
                ReportedByName = record.ReportedByName,
                ReportedAt = record.ReportedAt,
                ApprovalStatus = record.ApprovalStatus,
                AbsenceState = record.AbsenceState,
                FitNoteStatus = FitNotes.StatusOf(record, today, SelfCertDays),
                Documents =
                [
                    .. record.Documents.Select(document => new SicknessDocumentDto
                    {
                        Id = document.Id,
                        Kind = document.Kind,
                        Name = document.Name,
                        FileId = document.FileId,
                        ValidFrom = document.ValidFrom,
                        ValidTo = document.ValidTo,
                        UploadedAt = document.UploadedAt,
                    }),
                ],
                SpellDays = spell,
                WorkingDaysLost = Round(Absences.WorkingDaysLost(
                    record, today, Pattern(patterns, record.EmployeeId), holidays)),
                LongTerm = Absences.IsLongTerm(record, today, LongTermDays),
                ExclusionCategory = record.ExclusionCategory,
                ExclusionReason = record.ExclusionReason,
                ReturnToWork = new ReturnToWorkDto
                {
                    Status = record.RtwStatus,
                    ReturnDate = record.RtwReturnDate,
                    LastSickDay = record.RtwLastSickDay,
                    InterviewRequired = record.RtwInterviewRequired,
                    InterviewHeld = record.RtwInterviewHeld,
                    InterviewDate = record.RtwInterviewDate,
                    InterviewerEmployeeId = record.RtwInterviewerEmployeeId,
                    InterviewerName = NameOf(people, record.RtwInterviewerEmployeeId),
                    SafeToReturn = record.RtwSafeToReturn,
                    Notes = record.RtwNotes,
                    AdjustmentsRecommended = record.RtwAdjustmentsRecommended,
                    OhReferralRecommended = record.RtwOhReferralRecommended,
                    CompletedAt = record.RtwCompletedAt,

                    // Owed, and owed for longer than the grace period. Derived
                    // from the date rather than set by a sweep, so it is true
                    // the moment it becomes true.
                    Overdue = record.RtwStatus == RtwStatus.Pending
                        && (record.RtwReturnDate ?? record.EndDate) is DateOnly back
                        && today.DayNumber - back.DayNumber > ReturnToWorkGraceDays,
                },
                UpdatedAt = record.UpdatedAt,
            };
        })];
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static IReadOnlySet<DayOfWeek> Pattern(
        IReadOnlyDictionary<Guid, IReadOnlySet<DayOfWeek>> patterns,
        Guid employeeId) =>
        patterns.TryGetValue(employeeId, out IReadOnlySet<DayOfWeek>? own)
            ? own
            : patterns.GetValueOrDefault(Guid.Empty, new HashSet<DayOfWeek>(WeekdayFallback));

    private static string? NameOf(IReadOnlyDictionary<Guid, Person> people, Guid? id) =>
        id is Guid key && people.TryGetValue(key, out Person? person) ? person.Name : null;

    private async Task<IReadOnlyDictionary<Guid, Person>> PeopleAsync(
        CancellationToken cancellationToken)
    {
        if (_people is not null)
        {
            return _people;
        }

        List<Person> rows = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Status != EmployeeStatus.Deleted)
            .Select(employee => new Person(
                employee.Id,
                employee.FirstName + " " + employee.LastName,
                employee.DepartmentId,
                null,
                employee.ManagerId,
                employee.WorkScheduleId))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        _people = rows
            .Select(person => person with
            {
                DepartmentName = person.DepartmentId is Guid id
                    ? departments.GetValueOrDefault(id)
                    : null,
            })
            .ToDictionary(person => person.Id);

        return _people;
    }

    // Which days each person works. Keyed by employee, with the tenant default
    // under Guid.Empty so somebody with no schedule of their own still gets
    // the company's week rather than a hardcoded Monday-to-Friday.
    private async Task<IReadOnlyDictionary<Guid, IReadOnlySet<DayOfWeek>>> PatternsAsync(
        CancellationToken cancellationToken)
    {
        if (_patterns is not null)
        {
            return _patterns;
        }

        List<WorkSchedule> schedules = await dbContext.WorkSchedules
            .AsNoTracking()
            .Where(schedule => schedule.IsActive)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, IReadOnlySet<DayOfWeek>> byScheduleId = schedules.ToDictionary(
            schedule => schedule.Id,
            schedule => (IReadOnlySet<DayOfWeek>)schedule.Days.Keys.ToHashSet());

        WorkSchedule? fallback = schedules.Find(schedule => schedule.IsDefault);

        Dictionary<Guid, IReadOnlySet<DayOfWeek>> patterns = new()
        {
            [Guid.Empty] = fallback is null
                ? new HashSet<DayOfWeek>(WeekdayFallback)
                : byScheduleId[fallback.Id],
        };

        foreach (Person person in (await PeopleAsync(cancellationToken)).Values)
        {
            if (person.WorkScheduleId is Guid scheduleId
                && byScheduleId.TryGetValue(scheduleId, out IReadOnlySet<DayOfWeek>? own))
            {
                patterns[person.Id] = own;
            }
        }

        _patterns = patterns;

        return _patterns;
    }

    private async Task<IReadOnlySet<DateOnly>> HolidaysAsync(CancellationToken cancellationToken)
    {
        _holidays ??=
        [
            .. await dbContext.PublicHolidays
                .AsNoTracking()
                .Select(holiday => holiday.Date)
                .ToListAsync(cancellationToken),
        ];

        return _holidays;
    }

    private static int WorkingDaysBetween(
        DateOnly from,
        DateOnly to,
        IReadOnlySet<DateOnly> holidays)
    {
        int days = 0;

        for (DateOnly day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                && !holidays.Contains(day))
            {
                days++;
            }
        }

        return days;
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (_zone is not null)
        {
            return _zone;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        _zone = settings?.TimeZoneId;

        return _zone;
    }

    private sealed record Person(
        Guid Id,
        string Name,
        Guid? DepartmentId,
        string? DepartmentName,
        Guid? ManagerId,
        Guid? WorkScheduleId);
}
