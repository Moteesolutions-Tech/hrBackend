namespace Motee.Domain.Offboarding;

public enum OffboardingStatus
{
    // Notice recorded, awaiting a decision.
    Pending,

    // The exit is agreed. Clearance can begin.
    Approved,

    // At least one clearance item is done. Distinct from Approved so "nobody has
    // started" and "half the departments have signed off" are not the same row.
    InProgress,

    // Everyone has signed off and the person has left.
    Completed,

    // The exit was turned down. The employee stays.
    Disapproved,

    // Notice withdrawn after being agreed. The employee stays.
    Reactivated,
}

public enum ExitReason
{
    Resignation,
    Termination,
    Redundancy,
    Retirement,
    ContractEnd,
    Other,
}

// The verbs, not the states. Two different actions can lead to the same place —
// disapproving and reactivating both leave the employee in post — and the reason they
// happened is what an exit record is for.
public enum OffboardingAction
{
    Approve,
    Disapprove,
    StartClearance,
    Complete,
    Reactivate,
}

// Which moves are legal, as a table rather than as conditionals spread across a service.
//
// The alternative is a chain of ifs in each endpoint, which is where "approve an already
// completed exit" and "complete a disapproved one" get in — each individually obvious,
// collectively unenforceable once there are five verbs and six states.
public static class OffboardingLifecycle
{
    private static readonly Dictionary<(OffboardingStatus From, OffboardingAction Action), OffboardingStatus>
        Transitions = new()
        {
            [(OffboardingStatus.Pending, OffboardingAction.Approve)] = OffboardingStatus.Approved,
            [(OffboardingStatus.Pending, OffboardingAction.Disapprove)] = OffboardingStatus.Disapproved,

            // Ticking the first clearance item is what makes it in progress. Without
            // this the state exists and nothing ever enters it.
            [(OffboardingStatus.Approved, OffboardingAction.StartClearance)] = OffboardingStatus.InProgress,

            // Completable from either. A leaver with no clearance items outstanding is
            // still a leaver, and forcing a tick to unlock the finish is bureaucracy the
            // system invents for itself.
            [(OffboardingStatus.Approved, OffboardingAction.Complete)] = OffboardingStatus.Completed,
            [(OffboardingStatus.InProgress, OffboardingAction.Complete)] = OffboardingStatus.Completed,

            // Notice can be withdrawn at any point before the person actually goes.
            [(OffboardingStatus.Pending, OffboardingAction.Reactivate)] = OffboardingStatus.Reactivated,
            [(OffboardingStatus.Approved, OffboardingAction.Reactivate)] = OffboardingStatus.Reactivated,
            [(OffboardingStatus.InProgress, OffboardingAction.Reactivate)] = OffboardingStatus.Reactivated,
            [(OffboardingStatus.Disapproved, OffboardingAction.Reactivate)] = OffboardingStatus.Reactivated,

            // Completed is deliberately absent. Once someone has left, bringing them
            // back is a rehire — a new record or an explicit reinstatement — not a
            // button that undoes a departure. EmployeeLifecycle already says the same
            // thing from the other side: Inactive moves to nothing.
        };

    // The exit is still live and the employee has notice served. Used to stop a second
    // record being opened for someone already leaving.
    public static bool IsOpen(OffboardingStatus status) =>
        status is OffboardingStatus.Pending
            or OffboardingStatus.Approved
            or OffboardingStatus.InProgress;

    // Nothing further can happen. The record stays for the history.
    public static bool IsFinal(OffboardingStatus status) =>
        AvailableFrom(status).Count == 0;

    public static bool CanApply(OffboardingStatus from, OffboardingAction action) =>
        Transitions.ContainsKey((from, action));

    public static OffboardingStatus? Next(OffboardingStatus from, OffboardingAction action) =>
        Transitions.TryGetValue((from, action), out OffboardingStatus next) ? next : null;

    // What the interface may offer from here. Served to the client so the rules live in
    // one place: a matrix duplicated in the frontend drifts, and the symptom is a button
    // that is enabled and then refused.
    public static IReadOnlyList<OffboardingAction> AvailableFrom(OffboardingStatus from) =>
        [.. Transitions.Keys
            .Where(key => key.From == from)
            .Select(key => key.Action)
            .Order()];
}
