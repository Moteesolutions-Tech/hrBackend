namespace Motee.Domain.Approvals;

// What a chain is approving. A string rather than an enum because tenants add their own
// categories at runtime, and a fixed list would need a release to accommodate a company
// that approves something we did not think of.
public static class ApprovalDocumentTypes
{
    public const string Onboarding = "onboarding";
    public const string OffboardingClearance = "offboarding_clearance";
    public const string LeaveRequest = "leave_request";
    public const string WorkforceRequest = "workforce_request";
    public const string JobRequisition = "job_requisition";
    public const string Contract = "contract";
    public const string PromotionRequest = "promotion_request";
    public const string TrainingRequest = "training_request";
    public const string AssetRequest = "asset_request";
    public const string ExpenseClaim = "expense_claim";

    public static readonly IReadOnlyList<string> BuiltIn =
    [
        WorkforceRequest, LeaveRequest, JobRequisition, Contract, Onboarding,
        OffboardingClearance, PromotionRequest, TrainingRequest, AssetRequest, ExpenseClaim,
    ];
}

// Who has to act on a step, expressed as a rule rather than a person — so a template
// outlives the people in it. "The line manager" stays correct when someone changes jobs;
// a named approver does not.
//
// Phase 1 resolves the two positional ones. ROLE:{id} comes with phase 2, and the enum
// is deliberately not extended until the resolver behind it exists — an option the
// interface offers and the backend cannot honour is worse than one it does not offer.
public enum ApproverResolver
{
    LineManager,
    DepartmentHead,
}

public enum ApprovalStatus
{
    // Being prepared. Not yet anybody else's problem.
    Draft,

    // With an approver, waiting.
    InProgress,

    Approved,
    Rejected,

    // Sent back for changes. The submitter still owns it.
    Returned,

    Cancelled,
}

public enum ApprovalStepStatus
{
    Pending,
    Approved,
    Rejected,
    Returned,

    // Nobody could be resolved for this step, or a fallback skipped it. Recorded rather
    // than silently passed, because "who approved this" must never answer "nobody, and
    // no trace of why".
    Skipped,
}

public enum ApprovalAction
{
    Submit,
    Approve,
    Reject,
    Return,
    Resubmit,
    Cancel,
}

// The instance-level machine. Steps drive it — approving the last step is what approves
// the whole thing — but the transitions a person can ask for are these.
public static class ApprovalLifecycle
{
    private static readonly Dictionary<(ApprovalStatus From, ApprovalAction Action), ApprovalStatus>
        Transitions = new()
        {
            [(ApprovalStatus.Draft, ApprovalAction.Submit)] = ApprovalStatus.InProgress,
            [(ApprovalStatus.Draft, ApprovalAction.Cancel)] = ApprovalStatus.Cancelled,

            // Decisions come from the step machine below, applied here.
            [(ApprovalStatus.InProgress, ApprovalAction.Approve)] = ApprovalStatus.Approved,
            [(ApprovalStatus.InProgress, ApprovalAction.Reject)] = ApprovalStatus.Rejected,
            [(ApprovalStatus.InProgress, ApprovalAction.Return)] = ApprovalStatus.Returned,
            [(ApprovalStatus.InProgress, ApprovalAction.Cancel)] = ApprovalStatus.Cancelled,

            // Returned is the submitter's again: fix it and send it back round.
            [(ApprovalStatus.Returned, ApprovalAction.Resubmit)] = ApprovalStatus.InProgress,
            [(ApprovalStatus.Returned, ApprovalAction.Cancel)] = ApprovalStatus.Cancelled,

            // Approved, Rejected and Cancelled are absent. A decided approval is the
            // evidence that it was decided; reopening it would let the record disagree
            // with what the people in it actually saw.
        };

    public static bool IsOpen(ApprovalStatus status) =>
        status is ApprovalStatus.Draft or ApprovalStatus.InProgress or ApprovalStatus.Returned;

    public static bool IsFinal(ApprovalStatus status) => AvailableFrom(status).Count == 0;

    public static bool CanApply(ApprovalStatus from, ApprovalAction action) =>
        Transitions.ContainsKey((from, action));

    public static ApprovalStatus? Next(ApprovalStatus from, ApprovalAction action) =>
        Transitions.TryGetValue((from, action), out ApprovalStatus next) ? next : null;

    public static IReadOnlyList<ApprovalAction> AvailableFrom(ApprovalStatus from) =>
        [.. Transitions.Keys.Where(key => key.From == from).Select(key => key.Action).Order()];
}

// What a decision on one step means for the chain as a whole. Kept apart from the
// instance machine because the two answer different questions: this one is "what happens
// to the run", the other is "what may a person ask for".
public static class ApprovalChainRules
{
    // Only the step currently in front of an approver may be decided. Without this, an
    // approver further down the chain could approve early and the order the template
    // describes would mean nothing.
    public static bool CanDecide(ApprovalStepStatus step, ApprovalStatus instance) =>
        step == ApprovalStepStatus.Pending && instance == ApprovalStatus.InProgress;

    // A rejection or a return ends the round immediately: there is no sense asking the
    // next approver about something the last one sent back.
    public static ApprovalStatus? Outcome(
        ApprovalStepStatus decision,
        bool isLastStep) => decision switch
        {
            ApprovalStepStatus.Rejected => ApprovalStatus.Rejected,
            ApprovalStepStatus.Returned => ApprovalStatus.Returned,

            // Approving or skipping the final step approves the whole chain; otherwise
            // the run continues and the instance stays as it is.
            ApprovalStepStatus.Approved or ApprovalStepStatus.Skipped =>
                isLastStep ? ApprovalStatus.Approved : null,

            _ => null,
        };

    // On resubmission every step starts again, including ones already approved.
    //
    // The alternative — resuming from the step that returned it — means the approvals
    // already given were given to a different document. Whoever signed off the original
    // never saw what finally went through, which is exactly the claim an approval chain
    // exists to be able to make.
    public static bool RestartsFromTheBeginning => true;
}
