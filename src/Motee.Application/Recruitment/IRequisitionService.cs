using Motee.Application.Common;
using Motee.Domain.Organisation;
using Motee.Domain.Recruitment;

namespace Motee.Application.Recruitment;

// Vacancies.
//
// The status is partly derived. Draft, pending approval, on hold, closed and
// cancelled are decisions; open, interviewing, offer stage and filled are read
// off the pipeline every time the vacancy is described, so the badge cannot
// drift from the candidates it claims to summarise.
public interface IRequisitionService
{
    Task<PagedResult<RequisitionDto>> ListAsync(
        RequisitionQuery query,
        CancellationToken cancellationToken = default);

    Task<RequisitionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RequisitionResult> CreateAsync(
        RequisitionRequest request,
        CancellationToken cancellationToken = default);

    Task<RequisitionResult> UpdateAsync(
        Guid id,
        RequisitionRequest request,
        CancellationToken cancellationToken = default);

    // What ought to be sorted out before this goes live. Returned rather than
    // enforced so the screen can show the whole list at once; publishing then
    // refuses only on the blocking ones.
    Task<IReadOnlyList<PublishWarningDto>> WarningsAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<RequisitionResult> PublishAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RequisitionResult> HoldAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RequisitionResult> ResumeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RequisitionResult> CloseAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<RequisitionResult> CancelAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<RequisitionOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record RequisitionDto
{
    public required Guid Id { get; init; }

    public string? Reference { get; init; }

    public required string PositionTitle { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? Department { get; init; }

    public Guid? HiringManagerId { get; init; }

    public string? HiringManager { get; init; }

    public Guid? RecruiterId { get; init; }

    public string? Recruiter { get; init; }

    public Guid? HrBusinessPartnerId { get; init; }

    public string? HrBusinessPartner { get; init; }

    public required IReadOnlyList<Guid> InterviewPanel { get; init; }

    public required IReadOnlyList<string> InterviewPanelNames { get; init; }

    public required EmploymentType EmploymentType { get; init; }

    // Partly derived from the pipeline, which is why it comes back as the enum
    // rather than being read off the row by the caller.
    public required RequisitionStatus Status { get; init; }

    public required HiringPriority HiringPriority { get; init; }

    public string? Location { get; init; }

    public required int Openings { get; init; }

    public decimal? SalaryMin { get; init; }

    public decimal? SalaryMax { get; init; }

    public string? Currency { get; init; }

    public string? JobDescription { get; init; }

    public string? Qualifications { get; init; }

    public required IReadOnlyList<string> RequiredSkills { get; init; }

    public DateOnly? TargetStartDate { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    // Past its closing date. Derived from the date, so it is right without a
    // job having run.
    public required bool HasExpired { get; init; }

    public required bool IsAcceptingApplications { get; init; }

    public required IReadOnlyList<CandidateStage> Stages { get; init; }

    public required PipelineCountsDto Pipeline { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }
}

// How the funnel stands. Counted rather than stored, so it cannot disagree
// with the candidates it describes.
public sealed record PipelineCountsDto
{
    public required int Openings { get; init; }

    public required int Hired { get; init; }

    public required int Remaining { get; init; }

    public required int Active { get; init; }

    public required int Rejected { get; init; }

    public required IReadOnlyDictionary<CandidateStage, int> ByStage { get; init; }

    public required int OffersSent { get; init; }

    public required int OffersAccepted { get; init; }

    public required int OffersRejected { get; init; }
}

public sealed record PublishWarningDto
{
    public required string Field { get; init; }

    public required string Message { get; init; }

    // Blocking stops the publish; advisory does not. The distinction is whether
    // the vacancy would waste an applicant's time or merely attract the wrong
    // ones.
    public required bool IsBlocking { get; init; }
}

public sealed record RequisitionRequest
{
    public required string PositionTitle { get; init; }

    public Guid? DepartmentId { get; init; }

    public Guid? HiringManagerId { get; init; }

    public Guid? RecruiterId { get; init; }

    public Guid? HrBusinessPartnerId { get; init; }

    public IReadOnlyList<Guid>? InterviewPanel { get; init; }

    public required EmploymentType EmploymentType { get; init; }

    public HiringPriority HiringPriority { get; init; } = HiringPriority.Medium;

    public string? Location { get; init; }

    public int Openings { get; init; } = 1;

    public decimal? SalaryMin { get; init; }

    public decimal? SalaryMax { get; init; }

    public string? Currency { get; init; }

    public string? JobDescription { get; init; }

    public string? Qualifications { get; init; }

    public IReadOnlyList<string>? RequiredSkills { get; init; }

    public DateOnly? TargetStartDate { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    // Which of the five stages this role uses. Applicants and Hired are put
    // back if left out, so a role cannot be configured with no way in or out.
    public IReadOnlyList<CandidateStage>? Stages { get; init; }
}

public sealed record RequisitionQuery : PagedQuery
{
    public RequisitionStatus? Status { get; init; }

    public Guid? DepartmentId { get; init; }

    public Guid? HiringManagerId { get; init; }

    // Only the ones still taking applications.
    public bool RecruitingOnly { get; init; }

    public string? Search { get; init; }
}

public enum RequisitionOutcome
{
    Succeeded,
    NotFound,

    TitleRequired,

    // Nobody is answerable for the hire.
    HiringManagerRequired,

    // Applicants would have nothing to read.
    JobDescriptionRequired,

    OpeningsNotPositive,

    SalaryRangeInverted,

    ExpiryBeforeToday,

    // Publishing something already published, holding something not open, and
    // so on. The vacancy's paperwork has its own order.
    NotInThatState,

    // Deleting a vacancy people have applied to. Closing it keeps the funnel;
    // deleting it would take the applicants with it.
    HasCandidates,
}

public sealed record RequisitionResult
{
    public required RequisitionOutcome Outcome { get; init; }

    public RequisitionDto? Requisition { get; init; }

    public bool Succeeded => Outcome == RequisitionOutcome.Succeeded;

    public static RequisitionResult Failed(RequisitionOutcome outcome) =>
        new() { Outcome = outcome };

    public static RequisitionResult Ok(RequisitionDto requisition) =>
        new() { Outcome = RequisitionOutcome.Succeeded, Requisition = requisition };
}
