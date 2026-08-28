namespace Motee.Domain.Onboarding;

// How far into their first months somebody is. Milestones, not decisions — which is why
// this is looser than the offboarding machine: nothing irreversible happens when a
// record moves from first week to thirty days.
public enum OnboardingStage
{
    // Signed, not started. Paperwork and equipment happen here.
    PreBoarding,

    DayOne,
    FirstWeek,
    ThirtyDay,
    SixtyDay,
    NinetyDay,

    Completed,
}

// What the joiner has done about their own pack. Distinct from the stage: somebody can be
// three weeks in with their documents still unsubmitted, and conflating the two hides
// exactly the people HR need to chase.
public enum OnboardingSubmission
{
    // Nothing filled in yet.
    NotStarted,

    // A saved draft exists.
    InProgress,

    // Sent to HR. From here the approval chain owns it.
    Submitted,
}

// Stages move freely forwards, and back. Deliberately permissive:
//
// A company that does not run sixty and ninety day check-ins should not have to walk
// every joiner through them, and somebody who advances a record by mistake should be
// able to put it back. Constraining that would be ceremony — no access is granted, no
// money moves, nothing is irreversible.
//
// The one rule worth enforcing is at the end, and it is not about order.
public static class OnboardingLifecycle
{
    // Completing is the only transition with a consequence: it is what says this person
    // is fully onboarded, and it is read by reporting and by the employee's own status.
    //
    // It requires the review to have been approved, because otherwise "onboarding
    // complete" means nothing — a record could be closed with the joiner's documents
    // never looked at, which is precisely the gap the review exists to close.
    public static bool CanComplete(OnboardingSubmission submission, bool reviewApproved) =>
        submission == OnboardingSubmission.Submitted && reviewApproved;

    // Submitting is the joiner's act, and only theirs to make once.
    public static bool CanSubmit(OnboardingSubmission submission) =>
        submission is OnboardingSubmission.NotStarted or OnboardingSubmission.InProgress;

    // Returned by HR: the pack is theirs again, so the submission goes back to being a
    // draft rather than staying "submitted" while they edit it.
    public static OnboardingSubmission OnReturned() => OnboardingSubmission.InProgress;

    public static bool IsFinal(OnboardingStage stage) => stage == OnboardingStage.Completed;

    // Advancing to a stage already passed is allowed; advancing to Completed is not,
    // because that one goes through CanComplete and its review check.
    public static bool CanMove(OnboardingStage from, OnboardingStage to) =>
        from != OnboardingStage.Completed && to != OnboardingStage.Completed;
}
