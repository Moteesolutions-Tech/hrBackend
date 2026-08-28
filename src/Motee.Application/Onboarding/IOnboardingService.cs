using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Employees;
using Motee.Domain.Onboarding;

namespace Motee.Application.Onboarding;

// Onboarding as a process: who is in their first months, how far through, and whether
// what they submitted has been looked at.
//
// The review is not implemented here. It is an approval chain, and the engine owns it —
// this service starts one, reads where it stands, and refuses to complete a record whose
// chain has not been approved. That is the whole of the relationship, and it points one
// way: onboarding knows about approvals, approvals knows nothing about onboarding.
public interface IOnboardingService
{
    Task<PagedResult<OnboardingDto>> ListAsync(
        OnboardingQuery query,
        CancellationToken cancellationToken = default);

    Task<OnboardingDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // The joiner's own record, for the screen they see. Separate from GetAsync because it
    // is answered from the caller's identity rather than an id they could change.
    Task<OnboardingDto?> MineAsync(CancellationToken cancellationToken = default);

    // Called when an employee is created. Idempotent: an employee has one onboarding, and
    // a second call returns the existing one rather than starting a rival.
    Task<OnboardingRecord> EnsureAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    // Called when a joiner completes the invitation wizard. Their submission is what
    // starts the review, so accepting the invite and submitting the pack are one act
    // rather than two the joiner has to remember.
    Task<OnboardingResult> SubmitAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    // HR moving somebody along: pre-boarding to day one, day one to first week. Free in
    // both directions — see OnboardingLifecycle for why.
    Task<OnboardingResult> MoveAsync(
        Guid id,
        OnboardingStage stage,
        CancellationToken cancellationToken = default);

    // The one guarded transition. Refuses while the review is unfinished.
    Task<OnboardingResult> CompleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public enum OnboardingOutcome
{
    Succeeded,
    NotFound,

    // Already submitted, already completed, or moving out of a completed record.
    NotAllowed,

    // Completing before the chain has approved. Kept distinct from NotAllowed because the
    // caller can say something useful about it — who it is still waiting on.
    ReviewIncomplete,

    // The review could not be started: no chain configured for onboarding, or none of its
    // steps resolved to anybody. The submission still stands; it is the review that
    // did not begin.
    ReviewUnstartable,
}

public sealed record OnboardingQuery : PagedQuery
{
    public OnboardingStage? Stage { get; init; }

    public OnboardingSubmission? Submission { get; init; }

    public Guid? DepartmentId { get; init; }

    // Name, email or job title.
    public string? Search { get; init; }

    // The people HR need to chase, which is the reason most of them open this screen:
    // started already and still nothing submitted.
    public bool? Overdue { get; init; }
}

public sealed record OnboardingResult
{
    public required OnboardingOutcome Outcome { get; init; }

    public OnboardingDto? Record { get; init; }

    public bool Succeeded => Outcome == OnboardingOutcome.Succeeded;

    public static OnboardingResult Failed(OnboardingOutcome outcome) => new() { Outcome = outcome };

    public static OnboardingResult Ok(OnboardingDto record) =>
        new() { Outcome = OnboardingOutcome.Succeeded, Record = record };
}

public sealed record OnboardingDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required string Email { get; init; }

    public string? JobTitle { get; init; }

    public string? DepartmentName { get; init; }

    public Guid? AvatarFileId { get; init; }

    // Signed after the query, like everywhere else avatars are shown. A link with a
    // lifetime cannot be produced by a projection.
    public string? AvatarUrl { get; init; }

    public required OnboardingStage Stage { get; init; }

    public required OnboardingSubmission Submission { get; init; }

    public required OnboardingMethod Method { get; init; }

    public DateOnly? StartDate { get; init; }

    // Their start date has passed and their pack is still not in. Derived rather than
    // stored: a stored flag would be right on the day it was written and wrong every
    // day after.
    public required bool IsOverdue { get; init; }

    // The review, straight from the engine. Null before they submit, and null for the
    // routes where HR entered everything themselves — reviewing your own data entry is
    // theatre, so those records skip it.
    public ApprovalDto? Review { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}
