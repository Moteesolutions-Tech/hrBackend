using Motee.Domain.Approvals;

namespace Motee.Application.Approvals;

// Turns "the line manager" into a person, for one subject.
//
// Separate from the approval service because it is the piece most likely to grow: phase
// 2 adds ROLE:{id}, phase 3 adds on-leave fallbacks, and neither should mean opening the
// file that decides what a rejection does.
public interface IApproverResolution
{
    Task<ResolvedApprover> ResolveAsync(
        ApproverResolver resolver,
        Guid? subjectEmployeeId,
        Guid? roleId = null,
        CancellationToken cancellationToken = default);

    // Whether this user may act on a step answered by a role. Asked at decision time
    // rather than at submission, because that is the point the answer has to be true —
    // somebody who joined HR this morning should be able to clear this morning's queue.
    Task<bool> HoldsRoleAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default);
}

// Who a step landed on, or why it landed on nobody.
//
// "Nobody" is a real answer and has to be carried rather than thrown: an employee with no
// manager recorded is ordinary, and the chain has to decide what to do about it based on
// whether the step was required.
public sealed record ResolvedApprover
{
    public Guid? EmployeeId { get; init; }

    // The account that can actually act. An employee without one cannot approve
    // anything, so a step resolved to them is unactionable even though somebody was
    // found — which is a different problem from finding nobody, and says so.
    public Guid? UserId { get; init; }

    public string? Name { get; init; }

    // Set instead of UserId when a role answers the step. The two are mutually
    // exclusive: a step lands either on one person or on a queue, never on both.
    public Guid? RoleId { get; init; }

    // Set when a delegation redirected this away from whoever it first resolved to. The
    // person above is the delegate; this says who it would have been.
    public StepDelegation? Delegation { get; init; }

    public required bool Found { get; init; }

    // Why not, in words a person can act on: "no manager is recorded for this employee".
    // Written into the step so an approval stuck at a skip explains itself without
    // anyone reading code.
    public string? Reason { get; init; }

    public static ResolvedApprover To(Guid employeeId, Guid? userId, string name) => new()
    {
        Found = true,
        EmployeeId = employeeId,
        UserId = userId,
        Name = name,
    };

    // A queue rather than a person. Name is the access level's, so a screen can say
    // "with HR Admin" the same way it says "with Ada Okafor".
    public static ResolvedApprover ToRole(Guid roleId, string name) => new()
    {
        Found = true,
        RoleId = roleId,
        Name = name,
    };

    public static ResolvedApprover Nobody(string reason) => new()
    {
        Found = false,
        Reason = reason,
    };
}
