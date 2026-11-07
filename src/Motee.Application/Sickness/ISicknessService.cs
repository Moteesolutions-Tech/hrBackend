using Motee.Application.Common;
using Motee.Domain.Sickness;

namespace Motee.Application.Sickness;

// Sickness and absence.
//
// Sickness used to be one leave type among many. It has its own records now
// because it needs things leave does not: a fit-note clock, a return-to-work
// conversation, a Bradford score, and a confidentiality rule — a manager is
// entitled to know somebody is off, and not to know what is wrong with them.
//
// Three things are derived rather than stored, each because storing them was
// the bug:
//
//   Fit-note status. Written once when the absence is reported, it says
//   "self-certified" forever, so a three-day absence that becomes three weeks
//   never reaches the list of notes to chase.
//
//   The Bradford score. A stored score is one edit away from a number nobody
//   can reproduce from the absences underneath it — and this number gets
//   quoted at people in meetings.
//
//   Whether a return-to-work is overdue. A date and a policy, not a flag.
//
// canViewMedical is resolved at the API edge, from the module's own data
// scope: Edit on time-payroll.sickness with reach beyond the holder's own
// team. Without it the clinical category and the employee's own words are
// withheld, which is the §10.8 rule the frontend applies too.
public interface ISicknessService
{
    Task<PagedResult<SicknessRecordDto>> ListAsync(
        SicknessQuery query,
        bool canViewMedical = false,
        CancellationToken cancellationToken = default);

    Task<SicknessRecordDto?> GetAsync(
        Guid id,
        bool canViewMedical = false,
        CancellationToken cancellationToken = default);

    // The figures every sickness screen shows. One read, so the records page,
    // the dashboard tile and the alerts page cannot disagree about how many
    // days the company lost.
    Task<SicknessSummaryDto> SummaryAsync(
        DateOnly? from = null,
        CancellationToken cancellationToken = default);

    // What needs chasing: fit notes outstanding, return-to-works overdue,
    // absences that have become long-term.
    Task<IReadOnlyList<SicknessAlertDto>> AlertsAsync(
        CancellationToken cancellationToken = default);

    Task<SicknessResult> ReportAsync(
        SicknessRequest request,
        CancellationToken cancellationToken = default);

    Task<SicknessResult> UpdateAsync(
        Guid id,
        SicknessRequest request,
        CancellationToken cancellationToken = default);

    Task<SicknessResult> ApproveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SicknessResult> RejectAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default);

    Task<SicknessResult> CancelAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default);

    // They are back. Closes the absence and opens the return-to-work.
    Task<SicknessResult> ReturnAsync(
        Guid id,
        ReturnRequest request,
        CancellationToken cancellationToken = default);

    Task<SicknessResult> CompleteReturnToWorkAsync(
        Guid id,
        ReturnToWorkRequest request,
        CancellationToken cancellationToken = default);

    Task<SicknessResult> AddDocumentAsync(
        Guid id,
        SicknessDocumentRequest request,
        CancellationToken cancellationToken = default);

    // Take an absence out of the Bradford score, with a reason and a name
    // against it. Required by the Equality Act for disability- and
    // pregnancy-related absence, and recorded rather than silently applied so
    // the decision can be reviewed.
    Task<SicknessResult> ExcludeAsync(
        Guid id,
        ExclusionRequest request,
        CancellationToken cancellationToken = default);

    Task<SicknessResult> ClearExclusionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // ── Bradford ────────────────────────────────────────────────────────────

    Task<BradfordDto> BradfordAsync(Guid employeeId, CancellationToken cancellationToken = default);

    // Everybody with a score, worst first. The board HR works from.
    Task<IReadOnlyList<BradfordRowDto>> BradfordBoardAsync(
        BradfordBoardQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record SicknessRecordDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public Guid? ManagerId { get; init; }

    public required SicknessKind Kind { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DayPortion StartPortion { get; init; }

    public TimeOnly? LeftWorkAt { get; init; }

    public DateOnly? EndDate { get; init; }

    public required DayPortion EndPortion { get; init; }

    public required bool OvernightShift { get; init; }

    public DateOnly? ExpectedReturnDate { get; init; }

    // Null when the caller may not see medical detail. Not "Other": a redacted
    // category that looks like a recorded one is worse than an absent field,
    // because a report would count it.
    public SicknessReason? Reason { get; init; }

    // The employee's own words. Only ever present with the medical grant.
    public string? ReasonDetail { get; init; }

    public required bool MedicalDetailWithheld { get; init; }

    public required bool Hospitalised { get; init; }

    public required DateTimeOffset NotifiedAt { get; init; }

    public required SicknessSource Source { get; init; }

    public string? ReportedByName { get; init; }

    public required DateTimeOffset ReportedAt { get; init; }

    public required SicknessApprovalStatus ApprovalStatus { get; init; }

    public required AbsenceState AbsenceState { get; init; }

    // Derived from the length of the absence and the notes on file, so it is
    // right today rather than right on the day it was reported.
    public required FitNoteStatus FitNoteStatus { get; init; }

    public required IReadOnlyList<SicknessDocumentDto> Documents { get; init; }

    // Calendar days, inclusive, running to today while the absence is open.
    public required int SpellDays { get; init; }

    public required decimal WorkingDaysLost { get; init; }

    public required bool LongTerm { get; init; }

    public ProtectedCategory? ExclusionCategory { get; init; }

    public string? ExclusionReason { get; init; }

    public required ReturnToWorkDto ReturnToWork { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SicknessDocumentDto
{
    public required Guid Id { get; init; }

    public required SicknessDocumentKind Kind { get; init; }

    public required string Name { get; init; }

    public Guid? FileId { get; init; }

    public DateOnly? ValidFrom { get; init; }

    public DateOnly? ValidTo { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }
}

public sealed record ReturnToWorkDto
{
    public required RtwStatus Status { get; init; }

    public DateOnly? ReturnDate { get; init; }

    public DateOnly? LastSickDay { get; init; }

    public required bool InterviewRequired { get; init; }

    public required bool InterviewHeld { get; init; }

    public DateOnly? InterviewDate { get; init; }

    public Guid? InterviewerEmployeeId { get; init; }

    public string? InterviewerName { get; init; }

    public required bool SafeToReturn { get; init; }

    public string? Notes { get; init; }

    public required bool AdjustmentsRecommended { get; init; }

    public required bool OhReferralRecommended { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    // Waiting on somebody, and has been for longer than the policy allows.
    public required bool Overdue { get; init; }
}

public sealed record SicknessQuery : PagedQuery
{
    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public SicknessApprovalStatus? ApprovalStatus { get; init; }

    public AbsenceState? AbsenceState { get; init; }

    public SicknessKind? Kind { get; init; }

    // Absences touching this window, not only those starting in it — a
    // fortnight already under way belongs in this month's list.
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public bool? FitNoteOutstanding { get; init; }

    public bool? ReturnToWorkPending { get; init; }

    public bool? LongTerm { get; init; }

    public string? Search { get; init; }
}

public sealed record SicknessSummaryDto
{
    public required DateOnly AsOf { get; init; }

    public DateOnly? From { get; init; }

    public required int Episodes { get; init; }

    // How many different people, which is the figure that says whether an
    // absence problem is widespread or one person having a hard year.
    public required int People { get; init; }

    public required decimal DaysLost { get; init; }

    public required decimal AverageSpellDays { get; init; }

    // Days lost ÷ (headcount × working days in the period).
    public required decimal AbsenceRatePercent { get; init; }

    public required int Ongoing { get; init; }

    public required int AwaitingApproval { get; init; }

    public required int FitNotesOutstanding { get; init; }

    public required int ReturnToWorkDue { get; init; }

    public required CountAndDaysDto LongTerm { get; init; }

    public required CountAndDaysDto ShortTerm { get; init; }
}

public sealed record CountAndDaysDto
{
    public required int Count { get; init; }

    public required decimal Days { get; init; }
}

public enum SicknessAlertKind
{
    FitNoteOutstanding,
    ReturnToWorkOverdue,
    LongTermAbsence,
    AwaitingApproval,
}

public sealed record SicknessAlertDto
{
    public required Guid RecordId { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required SicknessAlertKind Kind { get; init; }

    // A sentence, because it is shown as written.
    public required string Message { get; init; }

    public required bool Urgent { get; init; }

    public required DateOnly StartDate { get; init; }
}

public sealed record BradfordDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required DateOnly WindowStart { get; init; }

    public required DateOnly WindowEnd { get; init; }

    public required int Spells { get; init; }

    public required decimal Days { get; init; }

    public required decimal Score { get; init; }

    public required BradfordAction Action { get; init; }

    public required string Band { get; init; }

    public required IReadOnlyList<BradfordEpisodeDto> Episodes { get; init; }

    // What was left out and why. Shown, not hidden: somebody asked about their
    // score deserves the working, and an exclusion that nobody can see is an
    // exclusion nobody can check.
    public required IReadOnlyList<BradfordExclusionDto> Excluded { get; init; }
}

public sealed record BradfordEpisodeDto
{
    public required DateOnly Start { get; init; }

    public required DateOnly End { get; init; }

    public required decimal Days { get; init; }

    public required bool Clipped { get; init; }
}

public sealed record BradfordExclusionDto
{
    public required Guid RecordId { get; init; }

    public required string Reason { get; init; }
}

public sealed record BradfordRowDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public required int Spells { get; init; }

    public required decimal Days { get; init; }

    public required decimal Score { get; init; }

    public required BradfordAction Action { get; init; }

    public required string Band { get; init; }
}

public sealed record BradfordBoardQuery
{
    public Guid? DepartmentId { get; init; }

    // Only people at or above a band, for the "who needs a conversation" list.
    public BradfordAction? AtLeast { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed record SicknessRequest
{
    public required Guid EmployeeId { get; init; }

    public SicknessKind Kind { get; init; } = SicknessKind.Sickness;

    public required DateOnly StartDate { get; init; }

    public DayPortion StartPortion { get; init; } = DayPortion.Full;

    public TimeOnly? LeftWorkAt { get; init; }

    // Set only when they are already back by the time it is logged.
    public DateOnly? EndDate { get; init; }

    public DayPortion EndPortion { get; init; } = DayPortion.Full;

    public bool OvernightShift { get; init; }

    public DateOnly? ExpectedReturnDate { get; init; }

    public SicknessReason Reason { get; init; } = SicknessReason.Other;

    public string? ReasonDetail { get; init; }

    public bool Hospitalised { get; init; }

    public DateTimeOffset? NotifiedAt { get; init; }

    public SicknessSource Source { get; init; } = SicknessSource.Hr;

    public Guid? LinkedRecordId { get; init; }
}

public sealed record ReturnRequest
{
    public required DateOnly LastSickDay { get; init; }

    public DayPortion LastDayPortion { get; init; } = DayPortion.Full;

    // The day they were actually back at a desk. Not always the next day — a
    // Friday finish means a Monday return.
    public DateOnly? ReturnDate { get; init; }
}

public sealed record ReturnToWorkRequest
{
    public bool InterviewHeld { get; init; }

    public DateOnly? InterviewDate { get; init; }

    public Guid? InterviewerEmployeeId { get; init; }

    public required bool SafeToReturn { get; init; }

    public string? Notes { get; init; }

    public bool AdjustmentsRecommended { get; init; }

    public bool OhReferralRecommended { get; init; }
}

public sealed record SicknessDocumentRequest
{
    public required SicknessDocumentKind Kind { get; init; }

    public required string Name { get; init; }

    public Guid? FileId { get; init; }

    public DateOnly? ValidFrom { get; init; }

    public DateOnly? ValidTo { get; init; }
}

public sealed record ExclusionRequest
{
    public required ProtectedCategory Category { get; init; }

    public required string Reason { get; init; }
}

public enum SicknessOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,

    // The draft broke a domain rule; Refusal says which.
    Refused,

    StartDateRequired,
    EndBeforeStart,
    ReasonRequired,
    DocumentNameRequired,

    // An absence over the same days for the same person. Two records for one
    // illness double the days on every figure and square the spell count.
    OverlapsAnother,
}

public sealed record SicknessResult
{
    public required SicknessOutcome Outcome { get; init; }

    public SicknessRecordDto? Record { get; init; }

    public SicknessRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == SicknessOutcome.Succeeded;

    public static SicknessResult Failed(SicknessOutcome outcome) => new() { Outcome = outcome };

    public static SicknessResult Refused(SicknessRefusal refusal) =>
        new() { Outcome = SicknessOutcome.Refused, Refusal = refusal };

    public static SicknessResult Ok(SicknessRecordDto record) =>
        new() { Outcome = SicknessOutcome.Succeeded, Record = record };
}
