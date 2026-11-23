using Motee.Application.Common;
using Motee.Application.Files;
using Motee.Domain.Files;
using Motee.Domain.Relations;

namespace Motee.Application.Relations;

// Employee-relations cases: grievance, disciplinary and everything adjacent,
// under one model and one eight-stage flow.
//
// Three permission keys read this — HR's case list, the admin grievance view
// and the employee's own form — and all three were shipped with nothing behind
// them.
public interface IErCaseService
{
    Task<PagedResult<CaseRowDto>> ListAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default);

    Task<CaseDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CaseSummaryDto> SummaryAsync(CancellationToken cancellationToken = default);

    // The cases the caller raised or is named on. The employee-facing screen,
    // which needs no module grant: somebody must be able to raise a grievance
    // about their own manager.
    Task<PagedResult<CaseRowDto>> MineAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default);

    Task<CaseResult> RaiseAsync(
        CaseRequest request,
        CancellationToken cancellationToken = default);

    Task<CaseResult> UpdateAsync(
        Guid id,
        CaseRequest request,
        CancellationToken cancellationToken = default);

    // Refused with the reasons when the stages in between have not recorded
    // what they exist to record.
    Task<CaseResult> MoveAsync(
        Guid id,
        CaseStage stage,
        CancellationToken cancellationToken = default);

    Task<CaseResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // ── the trail ───────────────────────────────────────────────────────────

    Task<CaseResult> AddNoteAsync(
        Guid id,
        NoteRequest request,
        CancellationToken cancellationToken = default);

    Task<CaseResult> RemoveNoteAsync(
        Guid id,
        Guid noteId,
        CancellationToken cancellationToken = default);

    Task<CaseResult> RecordMeetingAsync(
        Guid id,
        MeetingRequest request,
        CancellationToken cancellationToken = default);

    Task<CaseResult> RemoveMeetingAsync(
        Guid id,
        Guid meetingId,
        CancellationToken cancellationToken = default);

    Task<CaseResult> AttachEvidenceAsync(
        Guid id,
        EvidenceUpload upload,
        CancellationToken cancellationToken = default);

    Task<FileContent?> OpenEvidenceAsync(
        Guid id,
        Guid evidenceId,
        CancellationToken cancellationToken = default);

    Task<CaseResult> RemoveEvidenceAsync(
        Guid id,
        Guid evidenceId,
        CancellationToken cancellationToken = default);

    // The case's own history, read off the audit trail the change tracker
    // already fills, merged with the notes and meetings recorded on it. Nothing
    // is stored twice, so the log cannot disagree with the record.
    Task<IReadOnlyList<CaseActivityDto>> ActivityAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public enum CaseOutcomeKind
{
    Succeeded,
    NotFound,
    Refused,

    // The stage cannot be left yet, or the kind of case has no such stage.
    Blocked,

    EmployeeNotFound,

    // Restricted: the people named on the case, and nobody else.
    NotVisible,

    // The caller's account has no employee record, so there is nobody to raise
    // a case as.
    NoEmployeeRecord,
}

public sealed record CaseResult
{
    public required CaseOutcomeKind Outcome { get; init; }

    public CaseDto? Case { get; init; }

    public CaseRefusal Refusal { get; init; }

    // Why a stage move was turned down, in the words a screen can show.
    public IReadOnlyList<string> Blockers { get; init; } = [];

    public FileRejection Rejection { get; init; }

    public bool Succeeded => Outcome == CaseOutcomeKind.Succeeded;

    public static CaseResult Failed(CaseOutcomeKind outcome) => new() { Outcome = outcome };

    public static CaseResult Refused(CaseRefusal refusal) =>
        new() { Outcome = CaseOutcomeKind.Refused, Refusal = refusal };

    public static CaseResult Rejected(FileRejection rejection) =>
        new() { Outcome = CaseOutcomeKind.Refused, Rejection = rejection };

    public static CaseResult Stuck(IReadOnlyList<string> blockers) =>
        new() { Outcome = CaseOutcomeKind.Blocked, Blockers = blockers };

    public static CaseResult Ok(CaseDto row) =>
        new() { Outcome = CaseOutcomeKind.Succeeded, Case = row };

    public static CaseResult Done() => new() { Outcome = CaseOutcomeKind.Succeeded };
}

public sealed record CaseQuery : PagedQuery
{
    public CaseComplaintType? ComplaintType { get; init; }

    // Which side of the register: the employee record has a grievances tab and
    // a disciplinaries tab, each under its own permission key.
    public CaseGroup? Group { get; init; }

    public CaseStage? Stage { get; init; }

    public CasePriority? Priority { get; init; }

    public ConfidentialityLevel? ConfidentialityLevel { get; init; }

    public Guid? SubjectEmployeeId { get; init; }

    public Guid? InvestigatorEmployeeId { get; init; }

    public Guid? CaseOwnerEmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    // Everything not yet closed.
    public bool? OpenOnly { get; init; }

    // Past its target resolution date.
    public bool? OverdueOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record CaseRowDto
{
    public required Guid Id { get; init; }

    public required string CaseNumber { get; init; }

    public required CaseComplaintType ComplaintType { get; init; }

    public Guid? SubjectEmployeeId { get; init; }

    public string? SubjectName { get; init; }

    public string? SubjectDepartmentName { get; init; }

    public Guid? RaisedByEmployeeId { get; init; }

    public string? RaisedByName { get; init; }

    public required DateOnly RaisedOn { get; init; }

    public DateOnly? IncidentOn { get; init; }

    public required CaseStage Stage { get; init; }

    public required CasePriority Priority { get; init; }

    public required ConfidentialityLevel ConfidentialityLevel { get; init; }

    public Guid? InvestigatorEmployeeId { get; init; }

    public string? InvestigatorName { get; init; }

    public Guid? CaseOwnerEmployeeId { get; init; }

    public string? CaseOwnerName { get; init; }

    public DateOnly? TargetResolutionOn { get; init; }

    // Derived, never stored: a stored flag is wrong the next morning and a
    // stored day count keeps climbing on a case that closed last month.
    public required SlaState SlaState { get; init; }

    public required int DaysOpen { get; init; }

    public CaseOutcome? Outcome { get; init; }

    public DateOnly? OutcomeOn { get; init; }

    public required bool HasAppeal { get; init; }

    public DateOnly? ClosedOn { get; init; }

    public required int NoteCount { get; init; }

    public required int MeetingCount { get; init; }

    public required int EvidenceCount { get; init; }

    public required int WitnessCount { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CaseDto
{
    public required CaseRowDto Case { get; init; }

    public required string Description { get; init; }

    public DateOnly? InvestigationDueOn { get; init; }

    public DateOnly? OutcomeDueOn { get; init; }

    public required IReadOnlyList<CaseWitness> Witnesses { get; init; }

    public DateOnly? HearingOn { get; init; }

    public required IReadOnlyList<PanelMemberDto> HearingPanel { get; init; }

    public int? SuspensionDays { get; init; }

    public Guid? AppealReviewerEmployeeId { get; init; }

    public string? AppealReviewerName { get; init; }

    public string? AppealGrounds { get; init; }

    public string? RetentionPeriod { get; init; }

    // Filtered to what the caller may read: an HR-only note never reaches the
    // person the case is about.
    public required IReadOnlyList<CaseNoteDto> Notes { get; init; }

    public required IReadOnlyList<CaseMeetingDto> Meetings { get; init; }

    public required IReadOnlyList<CaseEvidenceDto> Evidence { get; init; }

    // ── derived ─────────────────────────────────────────────────────────────

    // The stages this kind of case actually walks, so the tracker shows the
    // real flow rather than eight chips regardless.
    public required IReadOnlyList<CaseStage> Flow { get; init; }

    // Where it can go next, and what is missing where it cannot.
    public required IReadOnlyList<StageOptionDto> Stages { get; init; }
}

public sealed record StageOptionDto
{
    public required CaseStage Stage { get; init; }

    public required bool Allowed { get; init; }

    public required IReadOnlyList<string> Blockers { get; init; }
}

public sealed record PanelMemberDto
{
    public required Guid EmployeeId { get; init; }

    public string? Name { get; init; }
}

public sealed record CaseNoteDto
{
    public required Guid Id { get; init; }

    public Guid? AuthorUserId { get; init; }

    public string? AuthorName { get; init; }

    public required string Content { get; init; }

    public required NoteVisibility Visibility { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record CaseMeetingDto
{
    public required Guid Id { get; init; }

    public required CaseMeetingKind MeetingKind { get; init; }

    public required DateOnly HeldOn { get; init; }

    public TimeOnly? HeldAt { get; init; }

    public required IReadOnlyList<string> Attendees { get; init; }

    public string? Notes { get; init; }

    public string? MeetingOutcome { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record CaseEvidenceDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required Guid StoredFileId { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }
}

public sealed record CaseActivityDto
{
    public required DateTimeOffset At { get; init; }

    public string? ActorName { get; init; }

    public required string Action { get; init; }

    public string? Detail { get; init; }
}

public sealed record CaseSummaryDto
{
    public required int Total { get; init; }

    public required int Open { get; init; }

    public required int Overdue { get; init; }

    public required int DueSoon { get; init; }

    public required int Unassigned { get; init; }

    public required int UnderAppeal { get; init; }

    // Averaged over the cases that have closed, which is the only set where
    // the figure means anything.
    public required int? AverageDaysToClose { get; init; }

    public required IReadOnlyDictionary<CaseStage, int> ByStage { get; init; }

    public required IReadOnlyDictionary<CaseComplaintType, int> ByType { get; init; }
}

public sealed record CaseRequest
{
    public CaseComplaintType ComplaintType { get; init; }

    public Guid? SubjectEmployeeId { get; init; }

    public DateOnly? IncidentOn { get; init; }

    public required string Description { get; init; }

    public CasePriority Priority { get; init; }

    public ConfidentialityLevel ConfidentialityLevel { get; init; }

    public Guid? InvestigatorEmployeeId { get; init; }

    public Guid? CaseOwnerEmployeeId { get; init; }

    public DateOnly? TargetResolutionOn { get; init; }

    public DateOnly? InvestigationDueOn { get; init; }

    public DateOnly? OutcomeDueOn { get; init; }

    public IReadOnlyList<CaseWitness>? Witnesses { get; init; }

    public DateOnly? HearingOn { get; init; }

    public IReadOnlyList<Guid>? HearingPanelEmployeeIds { get; init; }

    public CaseOutcome? Outcome { get; init; }

    public DateOnly? OutcomeOn { get; init; }

    public int? SuspensionDays { get; init; }

    public Guid? AppealReviewerEmployeeId { get; init; }

    public string? AppealGrounds { get; init; }

    public string? RetentionPeriod { get; init; }
}

public sealed record NoteRequest
{
    public required string Content { get; init; }

    // Defaults to the tightest: a note written without a thought about who
    // sees it should not be the one that reaches the person the case is about.
    public NoteVisibility Visibility { get; init; } = NoteVisibility.HrOnly;
}

public sealed record MeetingRequest
{
    public CaseMeetingKind MeetingKind { get; init; }

    public required DateOnly HeldOn { get; init; }

    public TimeOnly? HeldAt { get; init; }

    public required IReadOnlyList<string> Attendees { get; init; }

    public string? Notes { get; init; }

    public string? MeetingOutcome { get; init; }
}

public sealed record EvidenceUpload
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required Stream Content { get; init; }

    public required long SizeBytes { get; init; }
}
