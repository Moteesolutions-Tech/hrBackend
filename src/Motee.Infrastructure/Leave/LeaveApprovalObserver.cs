using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Domain.Approvals;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Leave;

// Keeps a leave request's status in step with the chain deciding it.
//
// Without this the request would stay Pending for ever and its days would sit reserved
// against a balance long after somebody refused them — the kind of error that shows up
// as "I have fewer days than I should" months later, with nothing to point at.
internal sealed class LeaveApprovalObserver(MoteeDbContext dbContext, TimeProvider timeProvider)
    : IApprovalObserver
{
    // The same string LeaveRequestService passes to StartAsync. A constant on both sides
    // rather than a literal, so the two cannot drift apart and leave decisions silently
    // reaching nobody.
    public string SubjectType => LeaveSubject.Type;

    public async Task OnSettledAsync(
        ApprovalDto approval,
        CancellationToken cancellationToken = default)
    {
        LeaveRequest? request = await dbContext.LeaveRequests
            .FirstOrDefaultAsync(candidate => candidate.Id == approval.SubjectId, cancellationToken);

        // Another tenant's request, or one already deleted. Not an error: the engine
        // notifies on subject type, and a mismatch here means there is simply nothing of
        // ours to update.
        if (request is null)
        {
            return;
        }

        LeaveRequestStatus settled = StatusFor(approval.Status);

        // Still in flight — an intermediate step approved, or returned for changes. The
        // days stay reserved either way, so there is nothing to record.
        if (settled == LeaveRequestStatus.Pending || request.Status == settled)
        {
            return;
        }

        // A cancelled request stays cancelled. Somebody who withdrew their leave and then
        // had the chain rejected out from under them has not un-withdrawn it, and letting
        // a late decision overwrite that would resurrect days they gave back.
        if (request.Status == LeaveRequestStatus.Cancelled)
        {
            return;
        }

        request.Status = settled;
        request.DecidedAt = timeProvider.GetUtcNow();
        request.UpdatedAt = request.DecidedAt.Value;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static LeaveRequestStatus StatusFor(ApprovalStatus status) => status switch
    {
        ApprovalStatus.Approved => LeaveRequestStatus.Approved,
        ApprovalStatus.Rejected => LeaveRequestStatus.Rejected,
        ApprovalStatus.Cancelled => LeaveRequestStatus.Cancelled,

        // Draft, InProgress and Returned all mean nobody has finished deciding.
        _ => LeaveRequestStatus.Pending,
    };
}

public static class LeaveSubject
{
    public const string Type = "leave_request";
}
