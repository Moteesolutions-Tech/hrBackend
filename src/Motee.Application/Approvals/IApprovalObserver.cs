namespace Motee.Application.Approvals;

// Told when an approval it cares about reaches a decision.
//
// The engine deliberately knows nothing about the modules that use it, but modules need
// to know when a chain finishes — a leave request has to become Approved so its days
// count against a balance, and reading that back through the approval tables on every
// balance query would be both slow and easy to forget.
//
// So the dependency inverts rather than reverses: the engine calls an interface it owns,
// modules implement it, and the engine still names none of them. Adding a module means
// adding an implementation, not editing the engine.
public interface IApprovalObserver
{
    // Which SubjectType this observer answers for — the same string the module passed to
    // StartAsync. Matched exactly, so two modules cannot accidentally receive each
    // other's decisions.
    string SubjectType { get; }

    // Called after the decision is committed, never inside its transaction. An observer
    // that threw mid-save would roll back an approval somebody had legitimately given;
    // one that throws here fails on its own, leaving the decision standing and the
    // failure visible in the log rather than presented to the approver as a rejected
    // click.
    Task OnSettledAsync(ApprovalDto approval, CancellationToken cancellationToken = default);
}
