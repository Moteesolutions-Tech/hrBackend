using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Files;
using Motee.Application.Relations;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Relations;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Relations;

// Employee-relations cases.
//
// Three things the client cannot do. The case number is minted here, not from
// the length of a list that hands two simultaneous callers the same one.
// Confidentiality is enforced rather than badged, so a restricted case reaches
// the people named on it and nobody else. And the activity log is read off the
// audit trail the change tracker already fills, so the case's history cannot
// disagree with the record of what changed.
internal sealed class ErCaseService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    IFileUploadService files,
    TimeProvider timeProvider) : IErCaseService
{
    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<PagedResult<CaseRowDto>> ListAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ErCase> cases = Filtered(dbContext.ErCases.AsNoTracking(), query);

        if (query.OverdueOnly == true)
        {
            DateOnly today = await _calendar.TodayAsync(cancellationToken);

            cases = cases.Where(row =>
                row.Stage != CaseStage.Closed
                && row.TargetResolutionOn != null
                && row.TargetResolutionOn < today);
        }

        // A restricted case reaches the people named on it. Narrowed in the
        // query rather than filtered afterwards, so the paging and the totals
        // are the caller's own rather than somebody else's.
        Guid? me = await currentEmployee.IdAsync(cancellationToken);

        cases = cases.Where(row =>
            row.ConfidentialityLevel != ConfidentialityLevel.Restricted
            || (me != null
                && (row.InvestigatorEmployeeId == me
                    || row.CaseOwnerEmployeeId == me
                    || row.AppealReviewerEmployeeId == me)));

        return await PageAsync(cases, query, cancellationToken);
    }

    public async Task<PagedResult<CaseRowDto>> MineAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return PagedResult<CaseRowDto>.Empty(query.Page, query.PageSize);
        }

        // Raised by me, or about me. A restricted case about somebody is not
        // for them — but one they raised themselves always is.
        IQueryable<ErCase> cases = Filtered(dbContext.ErCases.AsNoTracking(), query)
            .Where(row =>
                row.RaisedByEmployeeId == me
                || (row.SubjectEmployeeId == me
                    && row.ConfidentialityLevel != ConfidentialityLevel.Restricted));

        return await PageAsync(cases, query, cancellationToken);
    }

    public async Task<CaseDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ErCase? row = await dbContext.ErCases
            .AsNoTracking()
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (row is null)
        {
            return null;
        }

        Guid? me = await currentEmployee.IdAsync(cancellationToken);

        return CaseAccess.CanSee(row, me, holdsModule: true)
            ? await DescribeAsync(row, cancellationToken)
            : null;
    }

    public async Task<CaseSummaryDto> SummaryAsync(
        CancellationToken cancellationToken = default)
    {
        List<ErCase> cases = await dbContext.ErCases
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        IReadOnlyList<ErCase> closed =
            [.. cases.Where(row => row.Stage == CaseStage.Closed)];

        Dictionary<CaseStage, int> byStage = [];

        foreach (CaseStage stage in Enum.GetValues<CaseStage>())
        {
            byStage[stage] = cases.Count(row => row.Stage == stage);
        }

        Dictionary<CaseComplaintType, int> byType = [];

        foreach (IGrouping<CaseComplaintType, ErCase> group in cases
            .GroupBy(row => row.ComplaintType))
        {
            byType[group.Key] = group.Count();
        }

        return new CaseSummaryDto
        {
            Total = cases.Count,
            Open = cases.Count(row => row.Stage != CaseStage.Closed),
            Overdue = cases.Count(row => CaseSla.StateOf(row, today) == SlaState.Overdue),
            DueSoon = cases.Count(row => CaseSla.StateOf(row, today) == SlaState.DueSoon),
            Unassigned = cases.Count(row =>
                row.Stage != CaseStage.Closed && row.InvestigatorEmployeeId is null),
            UnderAppeal = cases.Count(row => row.Stage == CaseStage.Appeal),

            // Only the closed ones: an average that counts cases still running
            // reports a figure that falls every time somebody raises a case.
            AverageDaysToClose = closed.Count == 0
                ? null
                : (int)Math.Round(
                    closed.Average(row => CaseSla.DaysOpen(row, today)),
                    MidpointRounding.AwayFromZero),

            ByStage = byStage,
            ByType = byType,
        };
    }

    public async Task<CaseResult> RaiseAsync(
        CaseRequest request,
        CancellationToken cancellationToken = default)
    {
        Guid? me = await currentEmployee.IdAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        ErCase row = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,

            // The highest so far, read inside the same transaction as the
            // insert. The unique index is what makes the race safe; this makes
            // it rare.
            CaseNumber = CaseNumbers.Next(await HighestNumberAsync(cancellationToken)),
            Description = string.Empty,
            RaisedByEmployeeId = me,
            RaisedOn = today,
            CreatedAt = now,
        };

        Apply(row, request, now);

        CaseRefusal refusal = CaseRules.Refusal(row, today);

        if (refusal != CaseRefusal.None)
        {
            return CaseResult.Refused(refusal);
        }

        CaseOutcomeKind references = await ReferencesAsync(row, cancellationToken);

        if (references != CaseOutcomeKind.Succeeded)
        {
            return CaseResult.Failed(references);
        }

        dbContext.ErCases.Add(row);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CaseResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<CaseResult> UpdateAsync(
        Guid id,
        CaseRequest request,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, CaseOutcomeKind outcome) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return CaseResult.Failed(outcome);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        Apply(row, request, timeProvider.GetUtcNow());

        CaseRefusal refusal = CaseRules.Refusal(row, today);

        if (refusal != CaseRefusal.None)
        {
            return CaseResult.Refused(refusal);
        }

        CaseOutcomeKind references = await ReferencesAsync(row, cancellationToken);

        if (references != CaseOutcomeKind.Succeeded)
        {
            return CaseResult.Failed(references);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return CaseResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<CaseResult> MoveAsync(
        Guid id,
        CaseStage stage,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, CaseOutcomeKind outcome) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return CaseResult.Failed(outcome);
        }

        // The evidence count the gate reads lives on the case, so it is brought
        // up to date before the gate is asked.
        row.EvidenceCount = await dbContext.CaseEvidence
            .CountAsync(evidence => evidence.CaseId == id, cancellationToken);

        CaseAdvance advance = CaseFlows.CanAdvanceTo(row, stage);

        if (!advance.Allowed)
        {
            return CaseResult.Stuck(advance.Blockers);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        CaseRules.Moved(row, stage, today);

        row.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return CaseResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<CaseResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, CaseOutcomeKind outcome) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return CaseResult.Failed(outcome);
        }

        // Notes, meetings and evidence go with it in the database. The audit
        // trail does not: what happened to a case is the one thing deleting it
        // must not erase.
        dbContext.ErCases.Remove(row);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CaseResult.Done();
    }

    // ── the trail ───────────────────────────────────────────────────────────

    public async Task<CaseResult> AddNoteAsync(
        Guid id,
        NoteRequest request,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, CaseOutcomeKind outcome) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return CaseResult.Failed(outcome);
        }

        CaseRefusal refusal = CaseRules.NoteRefusal(request.Content);

        if (refusal != CaseRefusal.None)
        {
            return CaseResult.Refused(refusal);
        }

        dbContext.CaseNotes.Add(new CaseNote
        {
            Id = Guid.NewGuid(),
            TenantId = row.TenantId,
            CaseId = id,
            AuthorUserId = UserId(),
            Content = request.Content.Trim(),
            Visibility = request.Visibility,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        row.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return CaseResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<CaseResult> RemoveNoteAsync(
        Guid id,
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, CaseOutcomeKind outcome) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return CaseResult.Failed(outcome);
        }

        CaseNote? note = await dbContext.CaseNotes
            .FirstOrDefaultAsync(
                found => found.Id == noteId && found.CaseId == id, cancellationToken);

        if (note is null)
        {
            return CaseResult.Failed(CaseOutcomeKind.NotFound);
        }

        dbContext.CaseNotes.Remove(note);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CaseResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<CaseResult> RecordMeetingAsync(
        Guid id,
        MeetingRequest request,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, CaseOutcomeKind outcome) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return CaseResult.Failed(outcome);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        CaseMeeting meeting = new()
        {
            Id = Guid.NewGuid(),
            TenantId = row.TenantId,
            CaseId = id,
            MeetingKind = request.MeetingKind,
            HeldOn = request.HeldOn,
            HeldAt = request.HeldAt,
            Attendees =
            [
                .. request.Attendees
                    .Select(name => name?.Trim() ?? string.Empty)
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase),
            ],
            Notes = Blank(request.Notes),
            MeetingOutcome = Blank(request.MeetingOutcome),
            RecordedByUserId = UserId(),
            CreatedAt = now,
        };

        CaseRefusal refusal = CaseRules.MeetingRefusal(meeting, today);

        if (refusal != CaseRefusal.None)
        {
            return CaseResult.Refused(refusal);
        }

        dbContext.CaseMeetings.Add(meeting);

        // A hearing recorded is the hearing date, so the case does not need it
        // typed twice.
        if (request.MeetingKind == CaseMeetingKind.DisciplinaryHearing
            && row.HearingOn is null)
        {
            row.HearingOn = request.HeldOn;
        }

        row.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return CaseResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<CaseResult> RemoveMeetingAsync(
        Guid id,
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, CaseOutcomeKind outcome) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return CaseResult.Failed(outcome);
        }

        CaseMeeting? meeting = await dbContext.CaseMeetings
            .FirstOrDefaultAsync(
                found => found.Id == meetingId && found.CaseId == id, cancellationToken);

        if (meeting is null)
        {
            return CaseResult.Failed(CaseOutcomeKind.NotFound);
        }

        dbContext.CaseMeetings.Remove(meeting);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CaseResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<CaseResult> AttachEvidenceAsync(
        Guid id,
        EvidenceUpload upload,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, CaseOutcomeKind outcome) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return CaseResult.Failed(outcome);
        }

        FileUploadResult stored = await files.UploadAsync(
            new FileUpload
            {
                Purpose = FilePurpose.CaseEvidence,
                OwnerId = id,
                FileName = upload.FileName,
                ContentType = upload.ContentType,
                Content = upload.Content,
                SizeBytes = upload.SizeBytes,
            },
            cancellationToken);

        if (!stored.Succeeded)
        {
            return CaseResult.Rejected(stored.Rejection);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        dbContext.CaseEvidence.Add(new CaseEvidence
        {
            Id = Guid.NewGuid(),
            TenantId = row.TenantId,
            CaseId = id,

            // Falls back to the file's own name: a piece of evidence with no
            // label in the list is unfindable.
            Name = string.IsNullOrWhiteSpace(upload.Name) ? upload.FileName : upload.Name.Trim(),
            Description = Blank(upload.Description),
            StoredFileId = stored.File!.Id,
            UploadedByUserId = UserId(),
            UploadedAt = now,
        });

        // Kept on the case so the investigation gate can ask whether there is
        // any without a second query.
        row.EvidenceCount++;
        row.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return CaseResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<FileContent?> OpenEvidenceAsync(
        Guid id,
        Guid evidenceId,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, _) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return null;
        }

        CaseEvidence? evidence = await dbContext.CaseEvidence
            .AsNoTracking()
            .FirstOrDefaultAsync(
                found => found.Id == evidenceId && found.CaseId == id, cancellationToken);

        return evidence is null
            ? null
            : await files.OpenAsync(evidence.StoredFileId, cancellationToken);
    }

    public async Task<CaseResult> RemoveEvidenceAsync(
        Guid id,
        Guid evidenceId,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, CaseOutcomeKind outcome) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return CaseResult.Failed(outcome);
        }

        CaseEvidence? evidence = await dbContext.CaseEvidence
            .FirstOrDefaultAsync(
                found => found.Id == evidenceId && found.CaseId == id, cancellationToken);

        if (evidence is null)
        {
            return CaseResult.Failed(CaseOutcomeKind.NotFound);
        }

        dbContext.CaseEvidence.Remove(evidence);

        row.EvidenceCount = Math.Max(0, row.EvidenceCount - 1);
        row.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        await files.DeleteAsync(evidence.StoredFileId, cancellationToken);

        return CaseResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    // The case's history: what the change tracker recorded about the case
    // itself, plus the notes and meetings put on it. Nothing is written twice,
    // so the log cannot drift from the record.
    public async Task<IReadOnlyList<CaseActivityDto>> ActivityAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        (ErCase? row, _) = await MineOrRefusedAsync(id, cancellationToken);

        if (row is null)
        {
            return [];
        }

        List<AuditEntry> entries = await dbContext.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.EntityId == id)
            .ToListAsync(cancellationToken);

        List<CaseNote> notes = await dbContext.CaseNotes
            .AsNoTracking()
            .Where(note => note.CaseId == id)
            .ToListAsync(cancellationToken);

        List<CaseMeeting> meetings = await dbContext.CaseMeetings
            .AsNoTracking()
            .Where(meeting => meeting.CaseId == id)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> actors = await ActorsAsync(
            [
                .. notes
                    .Where(note => note.AuthorUserId is not null)
                    .Select(note => note.AuthorUserId!.Value)
                    .Concat(meetings
                        .Where(meeting => meeting.RecordedByUserId is not null)
                        .Select(meeting => meeting.RecordedByUserId!.Value))
                    .Distinct(),
            ],
            cancellationToken);

        return
        [
            .. entries
                .Select(entry => new CaseActivityDto
                {
                    At = entry.CreatedAt,
                    ActorName = entry.ActorName,
                    Action = entry.Action.ToString(),
                    Detail = entry.Description,
                })
                .Concat(notes.Select(note => new CaseActivityDto
                {
                    At = note.CreatedAt,
                    ActorName = note.AuthorUserId is Guid author
                        ? actors.GetValueOrDefault(author)
                        : null,
                    Action = "NoteAdded",
                    Detail = $"A {Spoken(note.Visibility)} note was added.",
                }))
                .Concat(meetings.Select(meeting => new CaseActivityDto
                {
                    At = meeting.CreatedAt,
                    ActorName = meeting.RecordedByUserId is Guid recorder
                        ? actors.GetValueOrDefault(recorder)
                        : null,
                    Action = "MeetingRecorded",
                    Detail =
                        $"{Spoken(meeting.MeetingKind)} on {meeting.HeldOn:d MMMM yyyy}, "
                        + $"{meeting.Attendees.Count} attending.",
                }))
                .OrderByDescending(item => item.At),
        ];
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private static IQueryable<ErCase> Filtered(IQueryable<ErCase> cases, CaseQuery query)
    {
        if (query.ComplaintType is CaseComplaintType type)
        {
            cases = cases.Where(row => row.ComplaintType == type);
        }

        if (query.Group is CaseGroup group)
        {
            IReadOnlyList<CaseComplaintType> kinds = CaseGroups.Of(group);

            cases = cases.Where(row => kinds.Contains(row.ComplaintType));
        }

        if (query.Stage is CaseStage stage)
        {
            cases = cases.Where(row => row.Stage == stage);
        }

        if (query.Priority is CasePriority priority)
        {
            cases = cases.Where(row => row.Priority == priority);
        }

        if (query.ConfidentialityLevel is ConfidentialityLevel level)
        {
            cases = cases.Where(row => row.ConfidentialityLevel == level);
        }

        if (query.SubjectEmployeeId is Guid subject)
        {
            cases = cases.Where(row => row.SubjectEmployeeId == subject);
        }

        if (query.InvestigatorEmployeeId is Guid investigator)
        {
            cases = cases.Where(row => row.InvestigatorEmployeeId == investigator);
        }

        if (query.CaseOwnerEmployeeId is Guid owner)
        {
            cases = cases.Where(row => row.CaseOwnerEmployeeId == owner);
        }

        if (query.OpenOnly == true)
        {
            cases = cases.Where(row => row.Stage != CaseStage.Closed);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            cases = cases.Where(row =>
                EF.Functions.ILike(row.CaseNumber, pattern, "\\")
                || EF.Functions.ILike(row.Description, pattern, "\\"));
        }

        return cases;
    }

    private async Task<PagedResult<CaseRowDto>> PageAsync(
        IQueryable<ErCase> cases,
        CaseQuery query,
        CancellationToken cancellationToken)
    {
        int total = await cases.CountAsync(cancellationToken);

        List<ErCase> page = await cases
            // Open first, then the most urgent, then the oldest — the order the
            // list is worked through.
            .OrderBy(row => row.Stage == CaseStage.Closed)
            .ThenByDescending(row => row.Priority)
            .ThenBy(row => row.RaisedOn)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<CaseRowDto>
        {
            Items = await RowsAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    private async Task<(ErCase? Row, CaseOutcomeKind Outcome)> MineOrRefusedAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        ErCase? row = await dbContext.ErCases
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (row is null)
        {
            return (null, CaseOutcomeKind.NotFound);
        }

        Guid? me = await currentEmployee.IdAsync(cancellationToken);

        return CaseAccess.CanSee(row, me, holdsModule: true)
            ? (row, CaseOutcomeKind.Succeeded)
            : (null, CaseOutcomeKind.NotVisible);
    }

    private async Task<string?> HighestNumberAsync(CancellationToken cancellationToken) =>
        await dbContext.ErCases
            .AsNoTracking()
            .OrderByDescending(row => row.CaseNumber.Length)
            .ThenByDescending(row => row.CaseNumber)
            .Select(row => row.CaseNumber)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<CaseOutcomeKind> ReferencesAsync(
        ErCase row,
        CancellationToken cancellationToken)
    {
        List<Guid> employeeIds =
        [
            .. new[]
                {
                    row.SubjectEmployeeId,
                    row.InvestigatorEmployeeId,
                    row.CaseOwnerEmployeeId,
                    row.AppealReviewerEmployeeId,
                }
                .Where(id => id is not null)
                .Select(id => id!.Value),
            .. row.HearingPanelEmployeeIds,
        ];

        List<Guid> distinct = [.. employeeIds.Distinct()];

        if (distinct.Count == 0)
        {
            return CaseOutcomeKind.Succeeded;
        }

        int found = await dbContext.Employees.CountAsync(
            employee => distinct.Contains(employee.Id)
                && employee.Status != EmployeeStatus.Deleted,
            cancellationToken);

        return found == distinct.Count
            ? CaseOutcomeKind.Succeeded
            : CaseOutcomeKind.EmployeeNotFound;
    }

    private static void Apply(ErCase row, CaseRequest request, DateTimeOffset now)
    {
        row.ComplaintType = request.ComplaintType;
        row.SubjectEmployeeId = request.SubjectEmployeeId;
        row.IncidentOn = request.IncidentOn;
        row.Description = request.Description?.Trim() ?? string.Empty;
        row.Priority = request.Priority;
        row.ConfidentialityLevel = request.ConfidentialityLevel;
        row.InvestigatorEmployeeId = request.InvestigatorEmployeeId;
        row.CaseOwnerEmployeeId = request.CaseOwnerEmployeeId;
        row.TargetResolutionOn = request.TargetResolutionOn;
        row.InvestigationDueOn = request.InvestigationDueOn;
        row.OutcomeDueOn = request.OutcomeDueOn;

        // Replaced rather than mutated: the jsonb converter compares by
        // serialising, so an in-place edit is not seen as a change.
        row.Witnesses =
        [
            .. (request.Witnesses ?? [])
                .Where(witness => !string.IsNullOrWhiteSpace(witness.Name))
                .Select(witness => new CaseWitness
                {
                    Name = witness.Name.Trim(),
                    Statement = Blank(witness.Statement),
                }),
        ];

        row.HearingOn = request.HearingOn;
        row.HearingPanelEmployeeIds = [.. (request.HearingPanelEmployeeIds ?? []).Distinct()];
        row.Outcome = request.Outcome;
        row.OutcomeOn = request.OutcomeOn;
        row.SuspensionDays = request.SuspensionDays;
        row.AppealReviewerEmployeeId = request.AppealReviewerEmployeeId;
        row.AppealGrounds = Blank(request.AppealGrounds);
        row.RetentionPeriod = Blank(request.RetentionPeriod);
        row.UpdatedAt = now;
    }

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private async Task<Dictionary<Guid, string>> ActorsAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken) =>
        userIds.Count == 0
            ? []
            : await dbContext.Users
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(user => userIds.Contains(user.Id))
                .Select(user => new
                {
                    user.Id,
                    Name = user.FirstName + " " + user.LastName,
                })
                .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);

    private async Task<Dictionary<Guid, Person>> PeopleAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        List<Guid> distinct = [.. employeeIds.Distinct()];

        return await dbContext.Employees
            .AsNoTracking()
            .Where(employee => distinct.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                employee.DepartmentId,
            })
            .ToDictionaryAsync(
                row => row.Id,
                row => new Person(
                    row.Name,
                    row.DepartmentId is Guid id ? departments.GetValueOrDefault(id) : null),
                cancellationToken);
    }

    private async Task<List<CaseRowDto>> RowsAsync(
        IReadOnlyList<ErCase> cases,
        CancellationToken cancellationToken)
    {
        if (cases.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. cases.Select(row => row.Id)];

        Dictionary<Guid, int> notes = await CountsAsync(
            dbContext.CaseNotes.Where(note => ids.Contains(note.CaseId))
                .Select(note => note.CaseId),
            cancellationToken);

        Dictionary<Guid, int> meetings = await CountsAsync(
            dbContext.CaseMeetings.Where(meeting => ids.Contains(meeting.CaseId))
                .Select(meeting => meeting.CaseId),
            cancellationToken);

        Dictionary<Guid, Person> people = await PeopleAsync(
            [
                .. cases
                    .SelectMany(row => new[]
                    {
                        row.SubjectEmployeeId,
                        row.RaisedByEmployeeId,
                        row.InvestigatorEmployeeId,
                        row.CaseOwnerEmployeeId,
                    })
                    .Where(id => id is not null)
                    .Select(id => id!.Value),
            ],
            cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return
        [
            .. cases.Select(row => new CaseRowDto
            {
                Id = row.Id,
                CaseNumber = row.CaseNumber,
                ComplaintType = row.ComplaintType,
                SubjectEmployeeId = row.SubjectEmployeeId,
                SubjectName = Named(people, row.SubjectEmployeeId)?.Name,
                SubjectDepartmentName = Named(people, row.SubjectEmployeeId)?.DepartmentName,
                RaisedByEmployeeId = row.RaisedByEmployeeId,
                RaisedByName = Named(people, row.RaisedByEmployeeId)?.Name,
                RaisedOn = row.RaisedOn,
                IncidentOn = row.IncidentOn,
                Stage = row.Stage,
                Priority = row.Priority,
                ConfidentialityLevel = row.ConfidentialityLevel,
                InvestigatorEmployeeId = row.InvestigatorEmployeeId,
                InvestigatorName = Named(people, row.InvestigatorEmployeeId)?.Name,
                CaseOwnerEmployeeId = row.CaseOwnerEmployeeId,
                CaseOwnerName = Named(people, row.CaseOwnerEmployeeId)?.Name,
                TargetResolutionOn = row.TargetResolutionOn,
                SlaState = CaseSla.StateOf(row, today),
                DaysOpen = CaseSla.DaysOpen(row, today),
                Outcome = row.Outcome,
                OutcomeOn = row.OutcomeOn,
                HasAppeal = row.HasAppeal,
                ClosedOn = row.ClosedOn,
                NoteCount = notes.GetValueOrDefault(row.Id),
                MeetingCount = meetings.GetValueOrDefault(row.Id),
                EvidenceCount = row.EvidenceCount,
                WitnessCount = row.Witnesses.Count,
                UpdatedAt = row.UpdatedAt,
            }),
        ];
    }

    private static async Task<Dictionary<Guid, int>> CountsAsync(
        IQueryable<Guid> caseIds,
        CancellationToken cancellationToken) =>
        await caseIds
            .GroupBy(id => id)
            .Select(group => new { CaseId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.CaseId, row => row.Count, cancellationToken);

    private async Task<CaseDto> DescribeAsync(
        ErCase row,
        CancellationToken cancellationToken)
    {
        Guid? me = await currentEmployee.IdAsync(cancellationToken);

        bool isSubject = me is Guid viewer && row.SubjectEmployeeId == viewer;

        bool isCaseTeam = me is Guid team
            && (row.InvestigatorEmployeeId == team
                || row.CaseOwnerEmployeeId == team
                || row.AppealReviewerEmployeeId == team);

        List<CaseNote> notes = await dbContext.CaseNotes
            .AsNoTracking()
            .Where(note => note.CaseId == row.Id)
            .OrderByDescending(note => note.CreatedAt)
            .ToListAsync(cancellationToken);

        List<CaseMeeting> meetings = await dbContext.CaseMeetings
            .AsNoTracking()
            .Where(meeting => meeting.CaseId == row.Id)
            .OrderByDescending(meeting => meeting.HeldOn)
            .ToListAsync(cancellationToken);

        List<CaseEvidence> evidence = await dbContext.CaseEvidence
            .AsNoTracking()
            .Where(item => item.CaseId == row.Id)
            .OrderByDescending(item => item.UploadedAt)
            .ToListAsync(cancellationToken);

        List<Guid> fileIds = [.. evidence.Select(item => item.StoredFileId)];

        Dictionary<Guid, StoredFile> stored = fileIds.Count == 0
            ? []
            : await dbContext.StoredFiles
                .AsNoTracking()
                .Where(file => fileIds.Contains(file.Id))
                .ToDictionaryAsync(file => file.Id, cancellationToken);

        Dictionary<Guid, string> authors = await ActorsAsync(
            [
                .. notes
                    .Where(note => note.AuthorUserId is not null)
                    .Select(note => note.AuthorUserId!.Value)
                    .Distinct(),
            ],
            cancellationToken);

        Dictionary<Guid, Person> people = await PeopleAsync(
            [
                .. row.HearingPanelEmployeeIds,
                .. row.AppealReviewerEmployeeId is Guid reviewer
                    ? (Guid[])[reviewer]
                    : [],
            ],
            cancellationToken);

        IReadOnlyList<CaseStage> flow = CaseFlows.For(row.ComplaintType);

        return new CaseDto
        {
            Case = (await RowsAsync([row], cancellationToken))[0],
            Description = row.Description,
            InvestigationDueOn = row.InvestigationDueOn,
            OutcomeDueOn = row.OutcomeDueOn,
            Witnesses = [.. row.Witnesses],
            HearingOn = row.HearingOn,
            HearingPanel =
            [
                .. row.HearingPanelEmployeeIds.Select(id => new PanelMemberDto
                {
                    EmployeeId = id,
                    Name = people.GetValueOrDefault(id)?.Name,
                }),
            ],
            SuspensionDays = row.SuspensionDays,
            AppealReviewerEmployeeId = row.AppealReviewerEmployeeId,
            AppealReviewerName = row.AppealReviewerEmployeeId is Guid appealReviewer
                ? people.GetValueOrDefault(appealReviewer)?.Name
                : null,
            AppealGrounds = row.AppealGrounds,
            RetentionPeriod = row.RetentionPeriod,

            // An HR-only note never reaches the person the case is about.
            Notes =
            [
                .. notes
                    .Where(note => !isSubject || isCaseTeam
                        || CaseAccess.CanSeeNote(note.Visibility, isSubject, isCaseTeam))
                    .Select(note => new CaseNoteDto
                    {
                        Id = note.Id,
                        AuthorUserId = note.AuthorUserId,
                        AuthorName = note.AuthorUserId is Guid author
                            ? authors.GetValueOrDefault(author)
                            : null,
                        Content = note.Content,
                        Visibility = note.Visibility,
                        CreatedAt = note.CreatedAt,
                    }),
            ],
            Meetings =
            [
                .. meetings.Select(meeting => new CaseMeetingDto
                {
                    Id = meeting.Id,
                    MeetingKind = meeting.MeetingKind,
                    HeldOn = meeting.HeldOn,
                    HeldAt = meeting.HeldAt,
                    Attendees = [.. meeting.Attendees],
                    Notes = meeting.Notes,
                    MeetingOutcome = meeting.MeetingOutcome,
                    CreatedAt = meeting.CreatedAt,
                }),
            ],
            Evidence =
            [
                .. evidence.Select(item =>
                {
                    StoredFile? file = stored.GetValueOrDefault(item.StoredFileId);

                    return new CaseEvidenceDto
                    {
                        Id = item.Id,
                        Name = item.Name,
                        Description = item.Description,
                        StoredFileId = item.StoredFileId,
                        FileName = file?.FileName ?? item.Name,
                        ContentType = file?.ContentType ?? "application/octet-stream",
                        SizeBytes = file?.SizeBytes ?? 0,
                        UploadedAt = item.UploadedAt,
                    };
                }),
            ],
            Flow = flow,
            Stages =
            [
                .. flow.Select(stage =>
                {
                    CaseAdvance advance = CaseFlows.CanAdvanceTo(row, stage);

                    return new StageOptionDto
                    {
                        Stage = stage,
                        Allowed = advance.Allowed,
                        Blockers = advance.Blockers,
                    };
                }),
            ],
        };
    }

    private static Person? Named(Dictionary<Guid, Person> people, Guid? employeeId) =>
        employeeId is Guid id ? people.GetValueOrDefault(id) : null;

    private static string Spoken(NoteVisibility visibility) => visibility switch
    {
        NoteVisibility.EmployeeVisible => "employee-visible",
        NoteVisibility.CaseTeam => "case-team",
        _ => "HR-only",
    };

    private static string Spoken(CaseMeetingKind kind) => kind switch
    {
        CaseMeetingKind.DisciplinaryHearing => "Disciplinary hearing",
        CaseMeetingKind.GrievanceMeeting => "Grievance meeting",
        CaseMeetingKind.AppealHearing => "Appeal hearing",
        _ => "Investigation meeting",
    };

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Person(string Name, string? DepartmentName);
}
