using Motee.Domain.Common;
using Motee.Domain.Organisation;

namespace Motee.Domain.Recruitment;

// Where a vacancy is.
//
// Ten values, but they answer two different questions, and keeping that
// straight is what stops the badge lying. Draft, PendingApproval, Approved,
// OnHold, Closed and Cancelled are facts about the vacancy's own paperwork:
// somebody decided them, and a draft is a draft however many people have
// applied to it. Open, Interviewing, OfferStage and Filled are facts about the
// pipeline, and are re-derived from the candidates rather than set.
public enum RequisitionStatus
{
    Draft,
    PendingApproval,
    Approved,

    // Published and taking applications.
    Open,

    // Somebody is in the interview stages.
    Interviewing,

    // Somebody is at offer.
    OfferStage,

    // Every opening has a hire in it.
    Filled,

    Closed,
    Cancelled,
    OnHold,
}

public enum HiringPriority
{
    Low,
    Medium,
    High,
    Urgent,
}

public class JobRequisition : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // REQ-YYYY-NNNNN, issued when the vacancy is first published and never
    // reissued. Quoted in emails and to agencies, so two vacancies sharing one
    // is worse than a vacancy having none.
    public string? Reference { get; set; }

    public required string PositionTitle { get; set; }

    public Guid? DepartmentId { get; set; }

    // Who owns the hire. A vacancy without one is a vacancy nobody is
    // answerable for, which is why publishing refuses without it.
    public Guid? HiringManagerId { get; set; }

    // Who runs the process day to day. Becomes the default owner of every
    // applicant, so candidates are not left in a shared inbox.
    public Guid? RecruiterId { get; set; }

    public Guid? HrBusinessPartnerId { get; set; }

    // The standing panel for this role, so an interview does not have to be
    // scheduled with an empty one.
    public List<Guid> InterviewPanel { get; set; } = [];

    public EmploymentType EmploymentType { get; set; }

    public RequisitionStatus Status { get; set; } = RequisitionStatus.Draft;

    public HiringPriority HiringPriority { get; set; } = HiringPriority.Medium;

    public string? Location { get; set; }

    // How many people this is funded for. The pipeline will not seat more hires
    // than this — Finance signed off a number, not an intention.
    public int Openings { get; set; } = 1;

    public decimal? SalaryMin { get; set; }

    public decimal? SalaryMax { get; set; }

    public string? Currency { get; set; }

    public string? JobDescription { get; set; }

    public string? Qualifications { get; set; }

    public List<string> RequiredSkills { get; set; } = [];

    public DateOnly? TargetStartDate { get; set; }

    // After this, no more applications. Null for a vacancy left open until
    // somebody closes it.
    public DateOnly? ExpiryDate { get; set; }

    // Which pipeline stages this role uses. Applicants and Hired are always in
    // it; the middle three can be switched off for a role that does not
    // interview or does not make formal offers.
    public List<CandidateStage> Stages { get; set; } =
    [
        CandidateStage.Applicants,
        CandidateStage.Interview,
        CandidateStage.Interviewed,
        CandidateStage.Offer,
        CandidateStage.Hired,
    ];

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
}

// REQ-2026-00124. Shaped like the expense reference and for the same reasons:
// per year so the number stays short and sortable, and parsed leniently so a
// hand-edited row cannot stop the next vacancy being published.
public static class RequisitionReferences
{
    public const string Prefix = "REQ";

    public static string Format(int year, int sequence) =>
        $"{Prefix}-{year}-{sequence.ToString().PadLeft(5, '0')}";

    public static bool TrySequenceOf(string? reference, int year, out int sequence)
    {
        sequence = 0;

        string prefix = $"{Prefix}-{year}-";

        return reference is not null
            && reference.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(reference[prefix.Length..], out sequence);
    }
}

public static class Requisitions
{
    // What the badge should say, read from the pipeline rather than from a
    // field somebody has to remember to set.
    //
    // Only the four pipeline-driven statuses are re-derived. The rest describe
    // the vacancy's paperwork and are left exactly as decided — deriving a
    // draft from its applicants would mean a vacancy nobody published could
    // report itself as interviewing.
    public static RequisitionStatus Derive(
        RequisitionStatus current,
        int openings,
        IReadOnlyCollection<Candidate> candidates)
    {
        if (current is not (RequisitionStatus.Open or RequisitionStatus.Interviewing
            or RequisitionStatus.OfferStage or RequisitionStatus.Filled))
        {
            return current;
        }

        // A hire counts however they were flagged afterwards. Somebody rejected
        // after they started is an offboarding question, not a vacancy one.
        int hired = candidates.Count(
            candidate => candidate.Stage == CandidateStage.Hired);

        if (hired >= Math.Max(1, openings))
        {
            return RequisitionStatus.Filled;
        }

        List<Candidate> live =
        [
            .. candidates.Where(candidate => candidate.Status == CandidateStatus.Active),
        ];

        if (live.Any(candidate => candidate.Stage == CandidateStage.Offer))
        {
            return RequisitionStatus.OfferStage;
        }

        return live.Any(candidate => candidate.Stage
            is CandidateStage.Interview or CandidateStage.Interviewed)
            ? RequisitionStatus.Interviewing
            : RequisitionStatus.Open;
    }

    // Still published and still recruiting. Not the same as Open: a role being
    // interviewed for is very much a live vacancy, and a filled one is not.
    public static bool IsRecruiting(RequisitionStatus status) =>
        status is RequisitionStatus.Open or RequisitionStatus.Interviewing
            or RequisitionStatus.OfferStage;

    // Closed by its own expiry date rather than by a job. Derived for the same
    // reason an announcement's is: a date is the answer, and a sweep that has
    // not run yet would leave the vacancy taking applications it should refuse.
    public static bool HasExpired(JobRequisition requisition, DateOnly today) =>
        requisition.ExpiryDate is DateOnly expiry && expiry < today;

    public static bool IsAcceptingApplications(JobRequisition requisition, DateOnly today) =>
        IsRecruiting(requisition.Status) && !HasExpired(requisition, today);
}
