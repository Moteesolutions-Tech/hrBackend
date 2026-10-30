using Motee.Domain.Common;

namespace Motee.Domain.Recruitment;

// The pipeline, as five linear steps with no sidings.
//
// Applicants is the entry and Hired the terminal; the middle three can be
// switched off per role. Deliberately a closed set rather than tenant-defined
// stages: the gates below are rules about what must have been recorded before
// somebody moves on, and a stage nobody has written a rule for is a stage
// anybody can be waved through.
public enum CandidateStage
{
    Applicants,

    // Scheduled for an interview, not yet rated.
    Interview,

    // Met and scored. Reached only by recording a scorecard, which is what
    // stops somebody arriving at an offer with no evidence behind them.
    Interviewed,

    Offer,

    Hired,
}

// Whether they are still in the running. Rejection is a status rather than a
// stage, so a rejected candidate keeps the stage they got to — "rejected at
// offer" and "rejected on sight" are different facts about the same process,
// and collapsing them loses the funnel.
public enum CandidateStatus
{
    Active,
    Rejected,
}

public class Candidate : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RequisitionId { get; set; }

    public required string FirstName { get; set; }

    public required string LastName { get; set; }

    public required string Email { get; set; }

    public string? Phone { get; set; }

    // Free text, not an enum. Companies source from boards we have never heard
    // of, and a fixed list would need a release to accept one.
    public string? Source { get; set; }

    public CandidateStage Stage { get; set; } = CandidateStage.Applicants;

    public CandidateStatus Status { get; set; } = CandidateStatus.Active;

    public List<string> Skills { get; set; } = [];

    public string? ExperienceSummary { get; set; }

    public string? Notes { get; set; }

    public string? LinkedIn { get; set; }

    public Guid? CvFileId { get; set; }

    public Guid? CoverLetterFileId { get; set; }

    // Who is chasing this person. Without an owner a pipeline is a shared
    // inbox, and "who is following up with Chidi?" has no answer. Defaults to
    // the vacancy's recruiter.
    public Guid? OwnerEmployeeId { get; set; }

    // The employee record created from this hire, closing the applicant →
    // offer → hired → onboarding → employee chain.
    public Guid? CreatedEmployeeId { get; set; }

    public string? RejectionReason { get; set; }

    public DateTimeOffset? RejectedAt { get; set; }

    public DateTimeOffset AppliedAt { get; set; }

    // When they last moved. Read as "how long have they sat here", which is the
    // number that tells somebody a candidate has gone quiet.
    public DateTimeOffset UpdatedAt { get; set; }

    public string FullName => $"{FirstName} {LastName}";
}

public static class Candidates
{
    // How long they have been where they are, in whole days.
    public static int DaysInStage(Candidate candidate, DateTimeOffset now) =>
        Math.Max(0, (int)(now - candidate.UpdatedAt).TotalDays);
}
