using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Common;
using Motee.Application.Health;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Employees;
using Motee.Domain.Health;
using Motee.Domain.Sickness;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Health;

// Occupational health referrals.
//
// The confidential and the narrow views are separate methods, and the narrow
// ones build OhManagerView — a shape with nowhere to put case notes. A flag on
// one method would mean a caller could forget it, and the failure would be a
// line manager reading somebody's case notes.
internal sealed class OhService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditTrail audit,
    TimeProvider timeProvider) : IOhService
{
    private const string Module = "time-payroll.occupational-health";

    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    // ── the board ──────────────────────────────────────────────────────────

    public async Task<PagedResult<OhCaseDto>> BoardAsync(
        OhCaseQuery query,
        CancellationToken cancellationToken = default)
    {
        (List<OhReferral> matching, DateOnly today) = await MatchingAsync(query, cancellationToken);

        List<OhReferral> page = [.. matching.Skip(query.Skip).Take(query.PageSize)];

        return new PagedResult<OhCaseDto>
        {
            Items = await DescribeAsync(page, today, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = matching.Count,
        };
    }

    public async Task<OhCaseDto?> CaseAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        OhReferral? referral = await dbContext.OhReferrals
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (referral is null)
        {
            return null;
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return (await DescribeAsync([referral], today, cancellationToken))[0];
    }

    public async Task<PagedResult<OhManagerView>> ManagerBoardAsync(
        OhCaseQuery query,
        CancellationToken cancellationToken = default)
    {
        (List<OhReferral> matching, DateOnly today) = await MatchingAsync(query, cancellationToken);

        List<OhReferral> page = [.. matching.Skip(query.Skip).Take(query.PageSize)];

        return new PagedResult<OhManagerView>
        {
            Items = await NarrowAsync(page, today, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = matching.Count,
        };
    }

    public async Task<OhManagerView?> ManagerCaseAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        OhReferral? referral = await dbContext.OhReferrals
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (referral is null)
        {
            return null;
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return (await NarrowAsync([referral], today, cancellationToken))[0];
    }

    public async Task<OhTally> SummaryAsync(CancellationToken cancellationToken = default)
    {
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<OhReferral> all = await dbContext.OhReferrals
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        Dictionary<Guid, int> adjustmentCounts = await AdjustmentCountsAsync(
            [.. all.Select(referral => referral.Id)], cancellationToken);

        List<OhReferral> open = [.. all.Where(OhStages.IsOpen)];

        return new OhTally
        {
            Open = open.Count,
            AwaitingProvider = open.Count(referral =>
                OhStages.AwaitsClinician(OhStages.Of(referral, today))),
            WithAdjustments = open.Count(referral =>
                adjustmentCounts.GetValueOrDefault(referral.Id) > 0),
            ReviewsDue = open.Count(referral => OhRules.ReviewIsDue(referral, today)),

            // The number this whole screen exists to keep at zero: somebody
            // past the threshold whom nobody has referred.
            PastThresholdUnreferred = open.Count(referral =>
                OhStages.Of(referral, today) is OhStage.ThresholdReached or OhStage.HrAlerted),

            Closed = all.Count - open.Count,
        };
    }

    // Absences past the threshold with no referral at all.
    //
    // The one thing the frontend cannot produce: its board is a list of
    // referrals, so an absence nobody has raised a referral for is invisible on
    // it. That is precisely the case this module exists to catch.
    public async Task<IReadOnlyList<OhCandidateDto>> CandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        DateOnly cutoff = today.AddDays(-OhStages.ReferralThresholdDays);

        // Anybody already on the board, open or closed. A closed referral still
        // counts: the absence was looked at, and resurfacing it as a candidate
        // would ask HR to do work they have already done.
        HashSet<Guid> alreadyRaised = [.. await dbContext.OhReferrals
            .AsNoTracking()
            .Select(referral => referral.EmployeeId)
            .ToListAsync(cancellationToken)];

        var absences = await dbContext.SicknessRecords
            .AsNoTracking()
            .Where(record => record.StartDate <= cutoff)
            .Select(record => new
            {
                record.Id,
                record.EmployeeId,
                record.StartDate,
                record.EndDate,
                record.AbsenceState,
                record.ApprovalStatus,
            })
            .ToListAsync(cancellationToken);

        List<OhCandidateDto> candidates = [];

        foreach (var absence in absences)
        {
            if (alreadyRaised.Contains(absence.EmployeeId))
            {
                continue;
            }

            // Still off. An absence somebody has returned from needs no
            // referral — the threshold is about continuous absence, and the
            // person is back.
            if (absence.AbsenceState == AbsenceState.Returned)
            {
                continue;
            }

            // Rejected and cancelled absences did not happen. The same rule the
            // sickness figures use, so the two screens cannot disagree about
            // who has been off.
            if (!SicknessLifecycle.Counts(absence.ApprovalStatus))
            {
                continue;
            }

            int days = today.DayNumber - absence.StartDate.DayNumber;

            candidates.Add(new OhCandidateDto
            {
                EmployeeId = absence.EmployeeId,
                EmployeeName = string.Empty,
                SicknessRecordId = absence.Id,
                AbsenceStartDate = absence.StartDate,
                AbsenceDays = days,
                DaysPastThreshold = days - OhStages.ReferralThresholdDays,
            });
        }

        Dictionary<Guid, Person> people = await PeopleAsync(
            [.. candidates.Select(candidate => candidate.EmployeeId)], cancellationToken);

        return
        [
            // Longest overdue first: that is the order somebody works this list
            // in, and the top of it is the most exposed.
            .. candidates
                .OrderByDescending(candidate => candidate.DaysPastThreshold)
                .Select(candidate => candidate with
                {
                    EmployeeName = people.TryGetValue(candidate.EmployeeId, out Person? person)
                        ? person.Name
                        : "Unknown",
                    Department = people.TryGetValue(candidate.EmployeeId, out Person? found)
                        ? found.Department
                        : null,
                }),
        ];
    }

    // ── raising and moving a case ──────────────────────────────────────────

    public async Task<OhResult> RaiseAsync(
        OhReferralRequest request,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.EmployeeId, cancellationToken);

        if (employee is null)
        {
            return OhResult.Refused(OhRefusal.EmployeeNotFound);
        }

        // One open case per person, or the board carries the same employee
        // twice with two different fitness opinions.
        bool alreadyOpen = await dbContext.OhReferrals
            .AsNoTracking()
            .Where(referral => referral.EmployeeId == request.EmployeeId)
            .AnyAsync(referral => referral.Stage != OhStage.Closed, cancellationToken);

        if (alreadyOpen)
        {
            return OhResult.Refused(OhRefusal.AlreadyOpen);
        }

        if (request.CaseOwnerEmployeeId is Guid ownerId
            && !await dbContext.Employees.AnyAsync(row => row.Id == ownerId, cancellationToken))
        {
            return OhResult.Refused(OhRefusal.OwnerNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        OhReferral referral = new()
        {
            Id = Guid.NewGuid(),
            TenantId = employee.TenantId,
            EmployeeId = request.EmployeeId,
            SicknessRecordId = request.SicknessRecordId,
            ReferenceNumber = await NextReferenceAsync(today, cancellationToken),
            AbsenceStartDate = request.AbsenceStartDate,
            ReferralDate = request.ReferralDate,
            AssessmentDate = request.AssessmentDate,
            Fitness = request.Fitness,
            ReasonableAdjustmentsConsidered = request.ReasonableAdjustmentsConsidered,
            ExpectedReturnDate = request.ExpectedReturnDate,
            CaseNotes = Trimmed(request.CaseNotes),
            CaseOwnerEmployeeId = request.CaseOwnerEmployeeId,
            ReviewDate = request.ReviewDate,

            // A referral raised with a date is already at Referred: recording
            // it and then having to move the stage would be two steps for one
            // decision.
            Stage = request.ReferralDate is null
                ? OhStage.AbsenceRecorded
                : OhStage.Referred,
            CreatedAt = now,
            UpdatedAt = now,
        };

        OhRefusal refusal = OhRules.CheckDates(referral);

        if (refusal != OhRefusal.None)
        {
            return OhResult.Refused(refusal);
        }

        dbContext.OhReferrals.Add(referral);

        audit.Record(
            AuditAction.Create,
            Module,
            $"Occupational health case {referral.ReferenceNumber} opened",
            entityId: referral.Id,
            entityType: nameof(OhReferral));

        await dbContext.SaveChangesAsync(cancellationToken);

        return OhResult.Ok((await DescribeAsync([referral], today, cancellationToken))[0]);
    }

    public async Task<OhResult> UpdateAsync(
        Guid id,
        OhReferralRequest request,
        CancellationToken cancellationToken = default)
    {
        OhReferral? referral = await dbContext.OhReferrals
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (referral is null)
        {
            return OhResult.Failed(OhOutcome.NotFound);
        }

        if (request.CaseOwnerEmployeeId is Guid ownerId
            && !await dbContext.Employees.AnyAsync(row => row.Id == ownerId, cancellationToken))
        {
            return OhResult.Refused(OhRefusal.OwnerNotFound);
        }

        referral.SicknessRecordId = request.SicknessRecordId;
        referral.AbsenceStartDate = request.AbsenceStartDate;
        referral.ReferralDate = request.ReferralDate;
        referral.AssessmentDate = request.AssessmentDate;
        referral.Fitness = request.Fitness;
        referral.ReasonableAdjustmentsConsidered = request.ReasonableAdjustmentsConsidered;
        referral.ExpectedReturnDate = request.ExpectedReturnDate;
        referral.CaseNotes = Trimmed(request.CaseNotes);
        referral.CaseOwnerEmployeeId = request.CaseOwnerEmployeeId;
        referral.ReviewDate = request.ReviewDate;

        OhRefusal refusal = OhRules.CheckDates(referral);

        if (refusal != OhRefusal.None)
        {
            return OhResult.Refused(refusal);
        }

        referral.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            $"Occupational health case {referral.ReferenceNumber} updated",
            entityId: referral.Id,
            entityType: nameof(OhReferral));

        await dbContext.SaveChangesAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return OhResult.Ok((await DescribeAsync([referral], today, cancellationToken))[0]);
    }

    public async Task<OhResult> SetAbsencesAsync(
        Guid id,
        IReadOnlyList<Guid> sicknessRecordIds,
        CancellationToken cancellationToken = default)
    {
        OhReferral? referral = await dbContext.OhReferrals
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (referral is null)
        {
            return OhResult.Failed(OhOutcome.NotFound);
        }

        // The same absence twice is one link, so a client sending a list with a
        // duplicate in it does not get the spell shown twice.
        List<Guid> wanted = [.. sicknessRecordIds.Distinct()];

        if (wanted.Count > 0)
        {
            // Fetched with their owner so the two refusals can be told apart. An id
            // that is not ours and an id that is somebody else's are different
            // mistakes, and "not found" for the second would send whoever is fixing
            // it looking in the wrong place.
            Dictionary<Guid, Guid> owners = await dbContext.SicknessRecords
                .AsNoTracking()
                .Where(record => wanted.Contains(record.Id))
                .ToDictionaryAsync(
                    record => record.Id, record => record.EmployeeId, cancellationToken);

            if (owners.Count != wanted.Count)
            {
                return OhResult.Refused(OhRefusal.AbsenceNotFound);
            }

            // The rule that matters. Linking somebody else's absence here would put
            // one person's health record on another person's file.
            if (owners.Values.Any(employeeId => employeeId != referral.EmployeeId))
            {
                return OhResult.Refused(OhRefusal.AbsenceBelongsToSomebodyElse);
            }
        }

        List<OhReferralAbsence> existing = await dbContext.OhReferralAbsences
            .Where(link => link.ReferralId == id)
            .ToListAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Only the difference is written. Clearing the lot and re-adding would churn
        // every LinkedAt on a save that changed one row, and the timestamps are the
        // record of when a spell was attached to the case.
        foreach (OhReferralAbsence link in existing
            .Where(link => !wanted.Contains(link.SicknessRecordId)))
        {
            dbContext.OhReferralAbsences.Remove(link);
        }

        HashSet<Guid> already = [.. existing.Select(link => link.SicknessRecordId)];

        foreach (Guid recordId in wanted.Where(recordId => !already.Contains(recordId)))
        {
            dbContext.OhReferralAbsences.Add(new OhReferralAbsence
            {
                Id = Guid.NewGuid(),
                TenantId = referral.TenantId,
                ReferralId = id,
                SicknessRecordId = recordId,
                LinkedAt = now,
            });
        }

        referral.UpdatedAt = now;

        // The absence ids are deliberately not in the description. AuditPolicy already
        // withholds the values on SicknessRecord and OhReferral, and spelling them into
        // an audit line would route around that.
        audit.Record(
            AuditAction.Update,
            Module,
            $"Occupational health case {referral.ReferenceNumber}: "
            + $"{wanted.Count} linked absence(s)",
            entityId: referral.Id,
            entityType: nameof(OhReferral));

        await dbContext.SaveChangesAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return OhResult.Ok((await DescribeAsync([referral], today, cancellationToken))[0]);
    }

    public async Task<OhResult> MoveAsync(
        Guid id,
        OhStageRequest request,
        CancellationToken cancellationToken = default)
    {
        OhReferral? referral = await dbContext.OhReferrals
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (referral is null)
        {
            return OhResult.Failed(OhOutcome.NotFound);
        }

        int adjustments = await dbContext.OhAdjustments
            .CountAsync(
                adjustment => adjustment.ReferralId == id
                    && adjustment.Stage != AdjustmentStage.Closed,
                cancellationToken);

        OhRefusal refusal = OhRules.CheckMove(referral, request.Stage, adjustments);

        if (refusal != OhRefusal.None)
        {
            return OhResult.Refused(refusal);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        referral.Stage = request.Stage;

        // Closing stamps the date, which is what stops a closed case's absence
        // counting for ever. Reopening clears it for the same reason.
        referral.ClosedOn = request.Stage == OhStage.Closed ? today : null;

        referral.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            string.IsNullOrWhiteSpace(request.Note)
                ? $"Case {referral.ReferenceNumber} moved to {Describe(request.Stage)}"
                : $"Case {referral.ReferenceNumber} moved to {Describe(request.Stage)}: "
                    + request.Note.Trim(),
            entityId: referral.Id,
            entityType: nameof(OhReferral));

        await dbContext.SaveChangesAsync(cancellationToken);

        return OhResult.Ok((await DescribeAsync([referral], today, cancellationToken))[0]);
    }

    public async Task<OhOutcome> RemoveAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        OhReferral? referral = await dbContext.OhReferrals
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (referral is null)
        {
            return OhOutcome.NotFound;
        }

        dbContext.OhReferrals.Remove(referral);

        audit.Record(
            AuditAction.Delete,
            Module,
            $"Occupational health case {referral.ReferenceNumber} removed",
            entityId: referral.Id,
            entityType: nameof(OhReferral));

        await dbContext.SaveChangesAsync(cancellationToken);

        return OhOutcome.Succeeded;
    }

    public async Task<IReadOnlyList<OhHistoryDto>> HistoryAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        List<AuditEntry> entries = await dbContext.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.EntityId == id)
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        return
        [
            .. entries.Select(entry => new OhHistoryDto
            {
                At = entry.CreatedAt,
                ActorName = entry.ActorName,
                Note = entry.Description,
            }),
        ];
    }

    // ── adjustments ────────────────────────────────────────────────────────

    public async Task<OhResult> AddAdjustmentAsync(
        Guid id,
        AdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        OhReferral? referral = await dbContext.OhReferrals
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (referral is null)
        {
            return OhResult.Failed(OhOutcome.NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return OhResult.Refused(OhRefusal.DescriptionRequired);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        OhAdjustment adjustment = new()
        {
            Id = Guid.NewGuid(),
            TenantId = referral.TenantId,
            ReferralId = id,
            SicknessRecordId = request.SicknessRecordId ?? referral.SicknessRecordId,
            Description = request.Description.Trim(),
            RecommendedOn = request.RecommendedOn ?? today,
            AgreedByEmployeeId = request.AgreedByEmployeeId,
            ImplementationOwnerEmployeeId = request.ImplementationOwnerEmployeeId,
            EffectiveDate = request.EffectiveDate,
            ReviewDate = request.ReviewDate,
            Stage = AdjustmentStage.Proposed,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.OhAdjustments.Add(adjustment);

        audit.Record(
            AuditAction.Create,
            Module,
            $"Adjustment proposed on case {referral.ReferenceNumber}",
            entityId: referral.Id,
            entityType: nameof(OhReferral));

        await dbContext.SaveChangesAsync(cancellationToken);

        return OhResult.Ok((await DescribeAsync([referral], today, cancellationToken))[0]);
    }

    public async Task<OhResult> UpdateAdjustmentAsync(
        Guid id,
        Guid adjustmentId,
        AdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        OhAdjustment? adjustment = await dbContext.OhAdjustments
            .FirstOrDefaultAsync(
                row => row.Id == adjustmentId && row.ReferralId == id, cancellationToken);

        if (adjustment is null)
        {
            return OhResult.Failed(OhOutcome.NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return OhResult.Refused(OhRefusal.DescriptionRequired);
        }

        adjustment.Description = request.Description.Trim();
        adjustment.SicknessRecordId = request.SicknessRecordId;
        adjustment.AgreedByEmployeeId = request.AgreedByEmployeeId;
        adjustment.ImplementationOwnerEmployeeId = request.ImplementationOwnerEmployeeId;
        adjustment.EffectiveDate = request.EffectiveDate;
        adjustment.ReviewDate = request.ReviewDate;

        if (request.RecommendedOn is DateOnly recommended)
        {
            adjustment.RecommendedOn = recommended;
        }

        adjustment.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return await ReloadAsync(id, cancellationToken);
    }

    public async Task<OhResult> MoveAdjustmentAsync(
        Guid id,
        Guid adjustmentId,
        AdjustmentStageRequest request,
        CancellationToken cancellationToken = default)
    {
        OhAdjustment? adjustment = await dbContext.OhAdjustments
            .FirstOrDefaultAsync(
                row => row.Id == adjustmentId && row.ReferralId == id, cancellationToken);

        if (adjustment is null)
        {
            return OhResult.Failed(OhOutcome.NotFound);
        }

        OhRefusal refusal = Adjustments.Check(adjustment, request.Stage);

        if (refusal != OhRefusal.None)
        {
            return OhResult.Refused(refusal);
        }

        adjustment.Stage = request.Stage;
        adjustment.UpdatedAt = timeProvider.GetUtcNow();

        string reference = await dbContext.OhReferrals
            .AsNoTracking()
            .Where(referral => referral.Id == id)
            .Select(referral => referral.ReferenceNumber)
            .FirstAsync(cancellationToken);

        audit.Record(
            AuditAction.Update,
            Module,
            $"Adjustment on case {reference} marked {request.Stage.ToString().ToLowerInvariant()}",
            entityId: id,
            entityType: nameof(OhReferral));

        await dbContext.SaveChangesAsync(cancellationToken);

        return await ReloadAsync(id, cancellationToken);
    }

    public async Task<OhOutcome> RemoveAdjustmentAsync(
        Guid id,
        Guid adjustmentId,
        CancellationToken cancellationToken = default)
    {
        OhAdjustment? adjustment = await dbContext.OhAdjustments
            .FirstOrDefaultAsync(
                row => row.Id == adjustmentId && row.ReferralId == id, cancellationToken);

        if (adjustment is null)
        {
            return OhOutcome.NotFound;
        }

        dbContext.OhAdjustments.Remove(adjustment);

        await dbContext.SaveChangesAsync(cancellationToken);

        return OhOutcome.Succeeded;
    }

    // ── shared ─────────────────────────────────────────────────────────────

    private async Task<(List<OhReferral> Matching, DateOnly Today)> MatchingAsync(
        OhCaseQuery query,
        CancellationToken cancellationToken)
    {
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        IQueryable<OhReferral> source = dbContext.OhReferrals.AsNoTracking();

        if (query.CaseOwnerEmployeeId is Guid owner)
        {
            source = source.Where(referral => referral.CaseOwnerEmployeeId == owner);
        }

        if (query.EmployeeId is Guid employeeId)
        {
            source = source.Where(referral => referral.EmployeeId == employeeId);
        }

        if (query.Fitness is OhFitness fitness)
        {
            source = source.Where(referral => referral.Fitness == fitness);
        }

        List<OhReferral> all = await source.ToListAsync(cancellationToken);

        Dictionary<Guid, int> adjustmentCounts = await AdjustmentCountsAsync(
            [.. all.Select(referral => referral.Id)], cancellationToken);

        // The tab and the stage are both questions about today, so neither can
        // be asked in SQL without writing the threshold rule twice. One row per
        // case is small enough to narrow in memory.
        List<OhReferral> matching =
        [
            .. all.Where(referral => OhBoard.Matches(
                referral,
                adjustmentCounts.GetValueOrDefault(referral.Id),
                query.Tab,
                today)),
        ];

        if (query.ReviewsDue is true)
        {
            matching = [.. matching.Where(referral => OhRules.ReviewIsDue(referral, today))];
        }

        if (query.DepartmentId is Guid departmentId)
        {
            HashSet<Guid> inDepartment = [.. await dbContext.Employees
                .AsNoTracking()
                .Where(employee => employee.DepartmentId == departmentId)
                .Select(employee => employee.Id)
                .ToListAsync(cancellationToken)];

            matching = [.. matching.Where(referral => inDepartment.Contains(referral.EmployeeId))];
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            Dictionary<Guid, Person> people = await PeopleAsync(
                [.. matching.Select(referral => referral.EmployeeId)], cancellationToken);

            string needle = query.Search.Trim();

            matching =
            [
                .. matching.Where(referral =>
                    referral.ReferenceNumber.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || (people.TryGetValue(referral.EmployeeId, out Person? person)
                            && person.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))),
            ];
        }

        // Longest absence first among open cases, closed last. That is the
        // order somebody works the board in: the person off longest is the most
        // exposed.
        return (
            [
                .. matching
                    .OrderBy(referral => referral.Stage == OhStage.Closed)
                    .ThenByDescending(referral => OhRules.AbsenceDays(referral, today) ?? 0)
                    .ThenBy(referral => referral.ReferenceNumber, StringComparer.Ordinal),
            ],
            today);
    }

    private async Task<OhResult> ReloadAsync(Guid id, CancellationToken cancellationToken)
    {
        OhCaseDto? reloaded = await CaseAsync(id, cancellationToken);

        return reloaded is null ? OhResult.Failed(OhOutcome.NotFound) : OhResult.Ok(reloaded);
    }

    private async Task<Dictionary<Guid, int>> AdjustmentCountsAsync(
        IReadOnlyList<Guid> referralIds,
        CancellationToken cancellationToken)
    {
        if (referralIds.Count == 0)
        {
            return [];
        }

        // Withdrawn adjustments do not count. A case whose only adjustment was
        // closed has none in place, and showing it on the Adjustments tab would
        // be reporting help that was withdrawn.
        List<Guid> rows = await dbContext.OhAdjustments
            .AsNoTracking()
            .Where(adjustment => referralIds.Contains(adjustment.ReferralId)
                && adjustment.Stage != AdjustmentStage.Closed)
            .Select(adjustment => adjustment.ReferralId)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(referralId => referralId)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private async Task<List<OhCaseDto>> DescribeAsync(
        IReadOnlyList<OhReferral> referrals,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (referrals.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. referrals.Select(referral => referral.Id)];

        List<OhAdjustment> adjustments = await dbContext.OhAdjustments
            .AsNoTracking()
            .Where(adjustment => ids.Contains(adjustment.ReferralId))
            .OrderBy(adjustment => adjustment.RecommendedOn)
            .ToListAsync(cancellationToken);

        // Every case's spells in one query rather than one per case, so a board of
        // twenty-five is two queries and not twenty-six.
        Dictionary<Guid, List<LinkedAbsenceDto>> linked =
            await LinkedAbsencesAsync(ids, cancellationToken);

        List<Guid> peopleIds =
        [
            .. referrals.Select(referral => referral.EmployeeId),
            .. referrals.Where(referral => referral.CaseOwnerEmployeeId is not null)
                .Select(referral => referral.CaseOwnerEmployeeId!.Value),
            .. adjustments.Where(adjustment => adjustment.AgreedByEmployeeId is not null)
                .Select(adjustment => adjustment.AgreedByEmployeeId!.Value),
            .. adjustments
                .Where(adjustment => adjustment.ImplementationOwnerEmployeeId is not null)
                .Select(adjustment => adjustment.ImplementationOwnerEmployeeId!.Value),
        ];

        Dictionary<Guid, Person> people = await PeopleAsync(peopleIds, cancellationToken);

        return
        [
            .. referrals.Select(referral =>
            {
                Person? person = people.GetValueOrDefault(referral.EmployeeId);

                return new OhCaseDto
                {
                    Id = referral.Id,
                    ReferenceNumber = referral.ReferenceNumber,
                    EmployeeId = referral.EmployeeId,
                    EmployeeName = person?.Name ?? "Unknown",
                    Initials = person?.Initials,
                    Department = person?.Department,
                    SicknessRecordId = referral.SicknessRecordId,
                    AbsenceStartDate = referral.AbsenceStartDate,
                    AbsenceDays = OhRules.AbsenceDays(referral, today),
                    ReferralDate = referral.ReferralDate,
                    AssessmentDate = referral.AssessmentDate,
                    Stage = OhStages.Of(referral, today),
                    RecordedStage = referral.Stage,
                    Fitness = referral.Fitness,
                    ReasonableAdjustmentsConsidered = referral.ReasonableAdjustmentsConsidered,
                    ExpectedReturnDate = referral.ExpectedReturnDate,
                    CaseNotes = referral.CaseNotes,
                    CaseOwnerEmployeeId = referral.CaseOwnerEmployeeId,
                    CaseOwnerName = referral.CaseOwnerEmployeeId is Guid ownerId
                        ? people.GetValueOrDefault(ownerId)?.Name
                        : null,
                    ReviewDate = referral.ReviewDate,
                    ReviewIsDue = OhRules.ReviewIsDue(referral, today),
                    ClosedOn = referral.ClosedOn,
                    Adjustments =
                    [
                        .. adjustments
                            .Where(adjustment => adjustment.ReferralId == referral.Id)
                            .Select(adjustment => Describe(adjustment, people, today)),
                    ],
                    LinkedAbsences = linked.GetValueOrDefault(referral.Id) ?? [],
                    NextAction = OhNextActions.Of(referral, today),
                    NextStages = OhStages.NextFrom(OhStages.Of(referral, today)),
                };
            }),
        ];
    }

    // The spells attached to each of a set of cases, oldest first.
    //
    // Joined here rather than held as a navigation property, for the same reason the
    // adjustments are: this service reads referrals with AsNoTracking and projects them,
    // and a lazy navigation would turn a board into a query per row.
    private async Task<Dictionary<Guid, List<LinkedAbsenceDto>>> LinkedAbsencesAsync(
        IReadOnlyList<Guid> referralIds,
        CancellationToken cancellationToken)
    {
        List<Link> links = await dbContext.OhReferralAbsences
            .AsNoTracking()
            .Where(link => referralIds.Contains(link.ReferralId))
            .Join(
                dbContext.SicknessRecords.AsNoTracking(),
                link => link.SicknessRecordId,
                record => record.Id,
                (link, record) => new Link(
                    link.ReferralId,
                    record.Id,
                    record.StartDate,
                    record.EndDate,
                    record.Reason,
                    record.AbsenceState))
            .ToListAsync(cancellationToken);

        return links
            .GroupBy(link => link.ReferralId)
            .ToDictionary(
                group => group.Key,
                group =>
                    group
                        .OrderBy(link => link.StartDate)
                        .ThenBy(link => link.Id)
                        .Select(link => new LinkedAbsenceDto
                        {
                            Id = link.Id,
                            StartDate = link.StartDate,
                            EndDate = link.EndDate,

                            // Inclusive of both ends. Null while somebody is still off:
                            // an open spell has no length yet, and counting to today
                            // would read as a closed one.
                            Days = link.EndDate is DateOnly end
                                ? (end.DayNumber - link.StartDate.DayNumber) + 1
                                : null,
                            Reason = link.Reason,
                            State = link.State,
                        })
                        .ToList());
    }

    private sealed record Link(
        Guid ReferralId,
        Guid Id,
        DateOnly StartDate,
        DateOnly? EndDate,
        SicknessReason Reason,
        AbsenceState State);

    // The narrow view. Built into OhManagerView, which has nowhere to put the
    // case notes — so this path cannot leak them even if somebody edits it
    // carelessly later.
    private async Task<List<OhManagerView>> NarrowAsync(
        IReadOnlyList<OhReferral> referrals,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (referrals.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. referrals.Select(referral => referral.Id)];

        List<OhAdjustment> adjustments = await dbContext.OhAdjustments
            .AsNoTracking()
            .Where(adjustment => ids.Contains(adjustment.ReferralId)
                && adjustment.Stage != AdjustmentStage.Closed)
            .OrderBy(adjustment => adjustment.RecommendedOn)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, Person> people = await PeopleAsync(
            [.. referrals.Select(referral => referral.EmployeeId)], cancellationToken);

        return
        [
            .. referrals.Select(referral =>
            {
                Person? person = people.GetValueOrDefault(referral.EmployeeId);

                return new OhManagerView
                {
                    Id = referral.Id,
                    EmployeeId = referral.EmployeeId,
                    EmployeeName = person?.Name ?? "Unknown",
                    Initials = person?.Initials,
                    Department = person?.Department,
                    Fitness = referral.Fitness,
                    Adjustments =
                    [
                        .. adjustments
                            .Where(adjustment => adjustment.ReferralId == referral.Id)
                            .Select(adjustment => adjustment.Description),
                    ],
                    ExpectedReturnDate = referral.ExpectedReturnDate,
                    ReviewDate = referral.ReviewDate,
                };
            }),
        ];
    }

    private static AdjustmentDto Describe(
        OhAdjustment adjustment,
        Dictionary<Guid, Person> people,
        DateOnly today) => new()
        {
            Id = adjustment.Id,
            Description = adjustment.Description,
            RecommendedOn = adjustment.RecommendedOn,
            SicknessRecordId = adjustment.SicknessRecordId,
            AgreedByEmployeeId = adjustment.AgreedByEmployeeId,
            AgreedByName = adjustment.AgreedByEmployeeId is Guid agreedBy
                ? people.GetValueOrDefault(agreedBy)?.Name
                : null,
            ImplementationOwnerEmployeeId = adjustment.ImplementationOwnerEmployeeId,
            ImplementationOwnerName = adjustment.ImplementationOwnerEmployeeId is Guid owner
                ? people.GetValueOrDefault(owner)?.Name
                : null,
            EffectiveDate = adjustment.EffectiveDate,
            ReviewDate = adjustment.ReviewDate,
            Stage = adjustment.Stage,
            IsInPlace = Domain.Health.Adjustments.IsInPlace(adjustment),
            ReviewIsDue = Domain.Health.Adjustments.ReviewIsDue(adjustment, today),
            NextStages = Domain.Health.Adjustments.NextFrom(adjustment.Stage),
        };

    private sealed record Person
    {
        public required string Name { get; init; }

        public string? Initials { get; init; }

        public string? Department { get; init; }
    }

    private async Task<Dictionary<Guid, Person>> PeopleAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        List<Guid> distinct = [.. employeeIds.Distinct()];

        var rows = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => distinct.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                employee.FirstName,
                employee.LastName,
                employee.PreferredName,
                employee.Initials,
                Department = employee.DepartmentId == null
                    ? null
                    : dbContext.Departments
                        .Where(department => department.Id == employee.DepartmentId)
                        .Select(department => department.Name)
                        .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Id,
            row => new Person
            {
                Name = string.IsNullOrWhiteSpace(row.PreferredName)
                    ? $"{row.FirstName} {row.LastName}".Trim()
                    : $"{row.PreferredName} {row.LastName}".Trim(),
                Initials = row.Initials,
                Department = row.Department,
            });
    }

    // OH-2026-0001. Per year, so a reference says roughly when the case was
    // opened — and counted from what exists rather than from a list length, so
    // removing a case does not hand its number to the next one.
    private async Task<string> NextReferenceAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        string prefix = $"OH-{today.Year}-";

        List<string> existing = await dbContext.OhReferrals
            .AsNoTracking()
            .Where(referral => referral.ReferenceNumber.StartsWith(prefix))
            .Select(referral => referral.ReferenceNumber)
            .ToListAsync(cancellationToken);

        int highest = 0;

        foreach (string reference in existing)
        {
            if (int.TryParse(reference[prefix.Length..], out int number) && number > highest)
            {
                highest = number;
            }
        }

        return $"{prefix}{highest + 1:D4}";
    }

    private static string Describe(OhStage stage) => stage switch
    {
        OhStage.ThresholdReached => "threshold reached",
        OhStage.HrAlerted => "HR alerted",
        OhStage.AssessmentCompleted => "assessment completed",
        OhStage.RecommendationsReceived => "recommendations received",
        OhStage.HrReview => "HR review",
        OhStage.AdjustmentsImplemented => "adjustments implemented",
        OhStage.RtwCompleted => "return-to-work interview completed",
        _ => stage.ToString().ToLowerInvariant(),
    };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
