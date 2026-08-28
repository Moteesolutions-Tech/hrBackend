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

    public static ResolvedApprover Nobody(string reason) => new()
    {
        Found = false,
        Reason = reason,
    };
}
