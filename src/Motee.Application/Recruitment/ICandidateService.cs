using Motee.Application.Common;
using Motee.Domain.Recruitment;

namespace Motee.Application.Recruitment;

// Applicants and their progress.
//
// Moving somebody is the only interesting operation here, and it is gated:
// nobody leaves the interview stage unrated, and nobody is hired without an
// accepted offer. Those rules live in the domain rather than in this service
// because the frontend's board enforces them too, and a rule held in one client
// is a rule every other client ignores.
public interface ICandidateService
{
    Task<PagedResult<CandidateDto>> ListAsync(
        CandidateQuery query,
        CancellationToken cancellationToken = default);

    Task<CandidateDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CandidateResult> CreateAsync(
        CandidateRequest request,
        CancellationToken cancellationToken = default);

    Task<CandidateResult> UpdateAsync(
        Guid id,
        CandidateRequest request,
        CancellationToken cancellationToken = default);

    // Whether a move would be allowed, without making it. The board asks before
    // it lets a card be dropped, so the refusal reaches the user as a sentence
    // rather than as a snap-back.
    Task<MoveRefusal> CanMoveAsync(
        Guid id,
        CandidateStage to,
        CancellationToken cancellationToken = default);

    Task<CandidateResult> MoveAsync(
        Guid id,
        CandidateStage to,
        CancellationToken cancellationToken = default);

    Task<CandidateResult> RejectAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default);

    // Back into the running, at the stage they were rejected from.
    Task<CandidateResult> RestoreAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CandidateResult> AssignOwnerAsync(
        Guid id,
        Guid? ownerEmployeeId,
        CancellationToken cancellationToken = default);

    // ── evidence ────────────────────────────────────────────────────────────

    Task<CandidateResult> ScoreAsync(
        Guid id,
        ScorecardRequest request,
        CancellationToken cancellationToken = default);

    Task<CandidateResult> SendOfferAsync(
        Guid id,
        OfferRequest request,
        CancellationToken cancellationToken = default);

    // What the candidate said. Accepted, rejected or withdrawn by the company.
    Task<CandidateResult> RespondToOfferAsync(
        Guid candidateId,
        Guid offerId,
        OfferResponseRequest request,
        CancellationToken cancellationToken = default);

    // ── the handoff ─────────────────────────────────────────────────────────

    // Turns a hire into somebody who works here: an employee record, an
    // invitation to complete the joiner wizard, and the onboarding behind it.
    //
    // This is the link the applicant → offer → hired → onboarding → employee
    // chain was missing. Until it existed, a hire was a card in a column and
    // somebody retyped their details into the employee form, which is how a
    // name gets spelled two ways and a start date gets lost.
    //
    // Idempotent on the employee it created, so inviting the same hire from the
    // stage table and from the drawer produces one person rather than two.
    Task<CandidateResult> SendToOnboardingAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed record CandidateDto
{
    public required Guid Id { get; init; }

    public required Guid RequisitionId { get; init; }

    public string? RequisitionTitle { get; init; }

    public required string Name { get; init; }

    public required string Email { get; init; }

    public string? Phone { get; init; }

    public string? Source { get; init; }

    public required CandidateStage Stage { get; init; }

    public required CandidateStatus Status { get; init; }

    public required IReadOnlyList<string> Skills { get; init; }

    public string? ExperienceSummary { get; init; }

    public string? Notes { get; init; }

    public string? LinkedIn { get; init; }

    public Guid? CvFileId { get; init; }

    public Guid? CoverLetterFileId { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public string? OwnerName { get; init; }

    public Guid? CreatedEmployeeId { get; init; }

    public string? RejectionReason { get; init; }

    public DateTimeOffset? RejectedAt { get; init; }

    public required DateTimeOffset AppliedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    // How long they have sat where they are. The number that says somebody has
    // gone quiet, which no amount of staring at a timestamp conveys.
    public required int DaysInStage { get; init; }

    public required IReadOnlyList<ScorecardDto> Scorecards { get; init; }

    // Across every rating. Null when nobody has met them, which is not zero.
    public decimal? Score { get; init; }

    public required IReadOnlyList<OfferDto> Offers { get; init; }
}

public sealed record ScorecardDto
{
    public required Guid Id { get; init; }

    public Guid? InterviewId { get; init; }

    public Guid? ByEmployeeId { get; init; }

    public string? ByName { get; init; }

    public required IReadOnlyList<ScorecardCriterion> Criteria { get; init; }

    // The average of the criteria, derived so it cannot disagree with them.
    public decimal? Overall { get; init; }

    public ScorecardRecommendation? Recommendation { get; init; }

    public string? Comment { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record OfferDto
{
    public required Guid Id { get; init; }

    public required OfferStatus Status { get; init; }

    public decimal? Salary { get; init; }

    public string? Currency { get; init; }

    public DateOnly? StartDate { get; init; }

    public string? Notes { get; init; }

    public Guid? AttachmentFileId { get; init; }

    public required DateTimeOffset SentAt { get; init; }

    public DateTimeOffset? RespondedAt { get; init; }

    public string? ResponseNote { get; init; }
}

public sealed record CandidateRequest
{
    public required Guid RequisitionId { get; init; }

    public required string FirstName { get; init; }

    public required string LastName { get; init; }

    public required string Email { get; init; }

    public string? Phone { get; init; }

    public string? Source { get; init; }

    public IReadOnlyList<string>? Skills { get; init; }

    public string? ExperienceSummary { get; init; }

    public string? Notes { get; init; }

    public string? LinkedIn { get; init; }

    public Guid? CvFileId { get; init; }

    public Guid? CoverLetterFileId { get; init; }

    // Left out, this falls to the vacancy's recruiter rather than to nobody.
    public Guid? OwnerEmployeeId { get; init; }
}

public sealed record CandidateQuery : PagedQuery
{
    public Guid? RequisitionId { get; init; }

    public CandidateStage? Stage { get; init; }

    public CandidateStatus? Status { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public string? Search { get; init; }
}

public sealed record ScorecardRequest
{
    public Guid? InterviewId { get; init; }

    public required IReadOnlyList<ScorecardCriterion> Criteria { get; init; }

    public ScorecardRecommendation? Recommendation { get; init; }

    public string? Comment { get; init; }
}

public sealed record OfferRequest
{
    public decimal? Salary { get; init; }

    public string? Currency { get; init; }

    public DateOnly? StartDate { get; init; }

    public string? Notes { get; init; }

    public Guid? AttachmentFileId { get; init; }
}

public sealed record OfferResponseRequest
{
    public required OfferStatus Status { get; init; }

    public string? ResponseNote { get; init; }
}

public enum CandidateOutcome
{
    Succeeded,
    NotFound,
    RequisitionNotFound,

    NameRequired,
    EmailRequired,
    EmailInvalid,

    // The same person applying to the same vacancy twice. Their first
    // application is the one with the history on it.
    AlreadyApplied,

    // The vacancy is not taking applications — unpublished, closed, or past its
    // expiry date.
    NotAcceptingApplications,

    ReasonRequired,

    // Restoring somebody who was never rejected.
    NotRejected,

    // A scorecard with no lines on it rates nothing.
    CriteriaRequired,

    ScoreOutOfRange,

    // Sending a second offer while one is still unanswered. Withdraw the first,
    // or record the answer.
    OfferOutstanding,

    OfferNotFound,

    // Answering an offer that already has an answer.
    OfferAlreadyAnswered,

    // Sent is the state an offer starts in, not an answer to it.
    NotAnAnswer,

    // Handing somebody to onboarding before they are hired. The pipeline's own
    // gates already guard the way into Hired, so this only ever fires when the
    // call skips the board entirely.
    NotHired,

    // Already handed over. Returned rather than silently doing it again,
    // because the second employee record would be a real person duplicated.
    AlreadyOnboarding,

    // The vacancy has no department, and an employee record needs one. Worth
    // its own outcome: the fix is on the vacancy, not on the candidate.
    DepartmentRequired,

    // The employee could not be created — usually because somebody already
    // works here under that address.
    CouldNotCreateEmployee,

    // The move itself was refused; the reason says which gate.
    MoveRefused,
}

public sealed record CandidateResult
{
    public required CandidateOutcome Outcome { get; init; }

    public CandidateDto? Candidate { get; init; }

    // Set only when the outcome is MoveRefused.
    public MoveRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == CandidateOutcome.Succeeded;

    public static CandidateResult Failed(CandidateOutcome outcome) =>
        new() { Outcome = outcome };

    public static CandidateResult Refused(MoveRefusal refusal) =>
        new() { Outcome = CandidateOutcome.MoveRefused, Refusal = refusal };

    public static CandidateResult Ok(CandidateDto candidate) =>
        new() { Outcome = CandidateOutcome.Succeeded, Candidate = candidate };
}
