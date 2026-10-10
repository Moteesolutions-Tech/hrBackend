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

    // Accepting the privacy notice, which gates the whole form. Null until they do.
    //
    // Owned here rather than on the employee: consent was given to be onboarded, at a
    // particular version of a particular notice, and tying it to the record keeps it
    // alongside what was collected under it.
    public PrivacyConsent? PrivacyConsent { get; set; }

    // Signing off the completed pack. Null until they submit, and the thing that makes
    // the submission an attestation rather than a form post.
    public JoinerDeclaration? Declaration { get; set; }

    // "Save & finish later": the client's own in-progress form state.
    //
    // Opaque on purpose. This is a partial wizard, mid-edit, and the backend has no
    // business interpreting half-entered fields — validating them would reject drafts for
    // being incomplete, which is what a draft is. It is written on save, handed back on
    // resume, and never read by anything else. The real columns are filled on submit.
    //
    // Stored as jsonb, so what comes back is equivalent rather than byte-identical:
    // Postgres reorders keys and normalises whitespace. That is the right trade — jsonb
    // refuses malformed JSON at the point of writing, where the alternative is storing
    // something that only fails when somebody tries to resume — but it does mean nothing
    // should compare this string to what it sent.
    public string? DraftJson { get; set; }

    // Which step they left off on, so resuming lands there rather than at the start.
    public int? DraftStep { get; set; }

    public DateTimeOffset? DraftSavedAt { get; set; }

    // When their pack last went to HR. Kept across a return-and-resubmit so "waiting
    // since" is answerable, which is the number that tells HR they are the holdup.
    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
