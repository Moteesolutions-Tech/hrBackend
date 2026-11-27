using Motee.Application.Common;
using Motee.Domain.Health;
using Motee.Domain.Sickness;

namespace Motee.Application.Health;

public enum OhOutcome
{
    Succeeded,
    NotFound,
    Refused,
}

// Occupational health referrals.
//
// The confidential and the narrow views are separate methods rather than one
// method with a flag, and that is deliberate. A flag means a caller can forget
// to pass it, or pass the wrong one, and the failure is silent — a line manager
// reading case notes. Two methods returning two shapes means the narrow path
// has nowhere to put the confidential parts, so the mistake cannot compile.
public interface IOhService
{
    // HR and occupational health staff: the whole case.
    Task<PagedResult<OhCaseDto>> BoardAsync(
        OhCaseQuery query,
        CancellationToken cancellationToken = default);

    Task<OhCaseDto?> CaseAsync(Guid id, CancellationToken cancellationToken = default);

    // Line managers, and anybody whose reach is their own reports or themselves.
    Task<PagedResult<OhManagerView>> ManagerBoardAsync(
        OhCaseQuery query,
        CancellationToken cancellationToken = default);

    Task<OhManagerView?> ManagerCaseAsync(Guid id, CancellationToken cancellationToken = default);

    Task<OhTally> SummaryAsync(CancellationToken cancellationToken = default);

    // Absences past the referral threshold with no referral raised. The list
    // this screen exists to keep empty, and the one thing the frontend cannot
    // produce because it has no view of absences without referrals.
    Task<IReadOnlyList<OhCandidateDto>> CandidatesAsync(
        CancellationToken cancellationToken = default);

    Task<OhResult> RaiseAsync(
        OhReferralRequest request,
        CancellationToken cancellationToken = default);

    Task<OhResult> UpdateAsync(
        Guid id,
        OhReferralRequest request,
        CancellationToken cancellationToken = default);

    Task<OhResult> MoveAsync(
        Guid id,
        OhStageRequest request,
        CancellationToken cancellationToken = default);

    Task<OhOutcome> RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    // The spells attached to a case. Set rather than added to: the screen hands over
    // the list it is showing, and anything missing from it has been unlinked.
    //
    // A case holds one triggering absence on the referral itself and a set of linked
    // ones here, because a long-running case is usually several spells — off, back for
    // a fortnight, off again — and one id could only ever name the first.
    Task<OhResult> SetAbsencesAsync(
        Guid id,
        IReadOnlyList<Guid> sicknessRecordIds,
        CancellationToken cancellationToken = default);

    // Read from the audit trail, which already records every stage change with
    // who made it and when. The field-level diff is withheld for this type, so
    // the history says what happened without reproducing a note.
    Task<IReadOnlyList<OhHistoryDto>> HistoryAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<OhResult> AddAdjustmentAsync(
        Guid id,
        AdjustmentRequest request,
        CancellationToken cancellationToken = default);

    Task<OhResult> UpdateAdjustmentAsync(
        Guid id,
        Guid adjustmentId,
        AdjustmentRequest request,
        CancellationToken cancellationToken = default);

    Task<OhResult> MoveAdjustmentAsync(
        Guid id,
        Guid adjustmentId,
        AdjustmentStageRequest request,
        CancellationToken cancellationToken = default);

    Task<OhOutcome> RemoveAdjustmentAsync(
        Guid id,
        Guid adjustmentId,
        CancellationToken cancellationToken = default);
}

public sealed record OhResult
{
    public required OhOutcome Outcome { get; init; }

    public OhCaseDto? Case { get; init; }

    public OhRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == OhOutcome.Succeeded;

    public static OhResult Ok(OhCaseDto value) =>
        new() { Outcome = OhOutcome.Succeeded, Case = value };

    public static OhResult Failed(OhOutcome outcome) => new() { Outcome = outcome };

    public static OhResult Refused(OhRefusal refusal) =>
        new() { Outcome = OhOutcome.Refused, Refusal = refusal };
}

// ── the case ───────────────────────────────────────────────────────────────

public sealed record OhCaseDto
{
    public required Guid Id { get; init; }

    public required string ReferenceNumber { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? Initials { get; init; }

    public string? Department { get; init; }

    public Guid? SicknessRecordId { get; init; }

    public DateOnly? AbsenceStartDate { get; init; }

    // Counted, and it stops on the day a case closed — otherwise a closed
    // case's absence grows for ever and the longest on the board are all people
    // who came back years ago.
    public int? AbsenceDays { get; init; }

    public DateOnly? ReferralDate { get; init; }

    public DateOnly? AssessmentDate { get; init; }

    // Where the case actually is. Before a referral this follows the absence
    // against the threshold, so a case cannot read "monitoring" with a
    // ninety-day absence behind it.
    public required OhStage Stage { get; init; }

    // What somebody last recorded, sent alongside so the screen can show that
    // the calendar has moved the case on.
    public required OhStage RecordedStage { get; init; }

    public required OhFitness Fitness { get; init; }

    public required bool ReasonableAdjustmentsConsidered { get; init; }

    public DateOnly? ExpectedReturnDate { get; init; }

    // Administrative only, and withheld from the manager view entirely.
    public string? CaseNotes { get; init; }

    public Guid? CaseOwnerEmployeeId { get; init; }

    public string? CaseOwnerName { get; init; }

    public DateOnly? ReviewDate { get; init; }

    public required bool ReviewIsDue { get; init; }

    public DateOnly? ClosedOn { get; init; }

    public required IReadOnlyList<AdjustmentDto> Adjustments { get; init; }

    // Every spell attached to the case, oldest first. Sent with the case rather than
    // fetched separately: the screen shows them in the same panel, and a second round
    // trip per case would make the board N+1.
    public required IReadOnlyList<LinkedAbsenceDto> LinkedAbsences { get; init; }

    // Derived from the stage, so it cannot keep saying "chase the appointment"
    // after the appointment happened.
    public OhNextAction? NextAction { get; init; }

    // Which stages this case may move to, built from the same rule the service
    // applies — so a picker cannot offer something the save will refuse.
    public required IReadOnlyList<OhStage> NextStages { get; init; }
}

// One spell attached to a case.
//
// Dates and a day count, and the reason category — not ReasonDetail, which is what the
// employee actually said and is special-category data behind the medical grant. A case
// panel listing spells needs to know a shoulder injury from a mental-health absence to
// read as a history at all; it does not need the sentence somebody typed.
public sealed record LinkedAbsenceDto
{
    public required Guid Id { get; init; }

    public required DateOnly StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    // Inclusive of both ends, and null while somebody is still off — an open spell has
    // no length yet, and showing today's count as the length reads as a closed one.
    public int? Days { get; init; }

    public required SicknessReason Reason { get; init; }

    public required AbsenceState State { get; init; }
}

public sealed record AdjustmentDto
{
    public required Guid Id { get; init; }

    public required string Description { get; init; }

    public required DateOnly RecommendedOn { get; init; }

    public Guid? SicknessRecordId { get; init; }

    public Guid? AgreedByEmployeeId { get; init; }

    public string? AgreedByName { get; init; }

    public Guid? ImplementationOwnerEmployeeId { get; init; }

    public string? ImplementationOwnerName { get; init; }

    public DateOnly? EffectiveDate { get; init; }

    public DateOnly? ReviewDate { get; init; }

    public required AdjustmentStage Stage { get; init; }

    public required bool IsInPlace { get; init; }

    public required bool ReviewIsDue { get; init; }

    public required IReadOnlyList<AdjustmentStage> NextStages { get; init; }
}

public sealed record OhCaseQuery : PagedQuery
{
    public OhBoardTab Tab { get; init; } = OhBoardTab.All;

    public Guid? CaseOwnerEmployeeId { get; init; }

    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public OhFitness? Fitness { get; init; }

    public string? Search { get; init; }

    // Only cases whose review date has arrived.
    public bool? ReviewsDue { get; init; }
}

// Somebody whose absence has passed the threshold with no referral raised.
public sealed record OhCandidateDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? Department { get; init; }

    public required Guid SicknessRecordId { get; init; }

    public required DateOnly AbsenceStartDate { get; init; }

    public required int AbsenceDays { get; init; }

    // How far past the threshold. The figure that says how late this is.
    public required int DaysPastThreshold { get; init; }
}

public sealed record OhHistoryDto
{
    public required DateTimeOffset At { get; init; }

    public string? ActorName { get; init; }

    public required string Note { get; init; }
}

// ── requests ───────────────────────────────────────────────────────────────

public sealed record OhReferralRequest
{
    public required Guid EmployeeId { get; init; }

    // The absence that triggered it. Optional: a referral can be raised without
    // a long absence behind it — a phased return after surgery, a manager's
    // concern about somebody still at work.
    public Guid? SicknessRecordId { get; init; }

    public DateOnly? AbsenceStartDate { get; init; }

    public DateOnly? ReferralDate { get; init; }

    public DateOnly? AssessmentDate { get; init; }

    public OhFitness Fitness { get; init; } = OhFitness.Pending;

    public bool ReasonableAdjustmentsConsidered { get; init; }

    public DateOnly? ExpectedReturnDate { get; init; }

    public string? CaseNotes { get; init; }

    public Guid? CaseOwnerEmployeeId { get; init; }

    public DateOnly? ReviewDate { get; init; }
}

public sealed record OhStageRequest
{
    public required OhStage Stage { get; init; }

    public string? Note { get; init; }
}

public sealed record AdjustmentRequest
{
    public required string Description { get; init; }

    public DateOnly? RecommendedOn { get; init; }

    public Guid? SicknessRecordId { get; init; }

    public Guid? AgreedByEmployeeId { get; init; }

    public Guid? ImplementationOwnerEmployeeId { get; init; }

    public DateOnly? EffectiveDate { get; init; }

    public DateOnly? ReviewDate { get; init; }
}

public sealed record AdjustmentStageRequest
{
    public required AdjustmentStage Stage { get; init; }
}
