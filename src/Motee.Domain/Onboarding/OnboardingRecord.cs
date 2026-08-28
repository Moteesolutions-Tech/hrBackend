using Motee.Domain.Common;

namespace Motee.Domain.Onboarding;

// A joiner's progress through their first months.
//
// Separate from the employee row on purpose. An employee is permanent; onboarding is a
// few months of their life, with its own dates, its own review and its own completion.
// Folding these six columns into Employee would leave them meaningless for the ninety
// percent of staff who are years past this, and would make "who is still onboarding" a
// question about nulls rather than about rows.
public class OnboardingRecord : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public OnboardingStage Stage { get; set; } = OnboardingStage.PreBoarding;

    public OnboardingSubmission Submission { get; set; } = OnboardingSubmission.NotStarted;

    // The approval chain reviewing their pack. Null until they submit, and the single
    // source of truth for where the review stands — this record does not keep its own
    // copy of that, because two places to look is how they end up disagreeing.
    public Guid? ApprovalInstanceId { get; set; }

    // When their pack last went to HR. Kept across a return-and-resubmit so "waiting
    // since" is answerable, which is the number that tells HR they are the holdup.
    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
