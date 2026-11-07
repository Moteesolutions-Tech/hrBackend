using Motee.Domain.Approvals;
using Motee.Domain.Common;
using Motee.Domain.Organisation;

namespace Motee.Domain.Workforce;

// How badly the headcount is needed. Drives how closely an approver looks,
// not whether they have to.
public enum WorkforceUrgency
{
    Low,
    Medium,
    High,
    Critical,
}

// Why the headcount is needed. A replacement for somebody who left is a
// different conversation from a new position, and "how many people" on its
// own told approvers nothing about what the money was for.
public enum HiringReason
{
    NewPosition,
    Replacement,
    InternalTransfer,
    Seasonal,
}

public enum VacancyType
{
    Permanent,
    FixedTerm,
    Contract,
    Temporary,
    Apprentice,
    Graduate,
}

// Where a request has got to.
//
// Only Draft, Cancelled and Converted are decisions somebody recorded here.
// Pending, approved and rejected are facts about the approval chain, re-read
// every time the request is described — a stored copy would be a badge that
// disagrees with the chain underneath it, and nothing would ever notice.
public enum WorkforceRequestStatus
{
    Draft,
    PendingApproval,
    Approved,
    Rejected,

    // Sent back for changes; the submitter still owns it.
    Returned,
    Cancelled,

    // A vacancy exists for it. The end of this record's job.
    Converted,
}

// A manager asking for headcount.
//
// The first link in the chain the product is built around: a gap on the
// headcount plan becomes a request, the request goes through approval, an
// approved request becomes a vacancy, the vacancy takes candidates, a
// candidate becomes a hire, and the hire becomes an employee. Without this
// row the chain starts at the vacancy, and nobody can answer who authorised
// it or against which budget.
public class WorkforceRequest : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // WFR-2026-00124, issued on submission. Quoted to Finance and in
    // approval emails, so two requests sharing one is worse than a draft
    // having none.
    public string? Reference { get; set; }

    public Guid? DepartmentId { get; set; }

    public Guid? BusinessUnitId { get; set; }

    public required string Position { get; set; }

    public string? Grade { get; set; }

    public int NumberOfHires { get; set; } = 1;

    public HiringReason HiringReason { get; set; } = HiringReason.NewPosition;

    public VacancyType VacancyType { get; set; } = VacancyType.Permanent;

    public WorkforceUrgency Urgency { get; set; } = WorkforceUrgency.Medium;

    // The case being made, in the requester's own words. What an approver
    // actually reads.
    public required string Justification { get; set; }

    public decimal? BudgetEstimate { get; set; }

    public string? Currency { get; set; }

    // Finance books the spend against this, so submission refuses without
    // one: a request nobody can charge is a request nobody can approve.
    public string? CostCentreCode { get; set; }

    public DateOnly? ExpectedStartDate { get; set; }

    public Guid? RequestedByEmployeeId { get; set; }

    // Kept beside the id so a request still says who raised it after that
    // person has left and their employee row is gone.
    public string? RequestedByName { get; set; }

    // The chain running against this request. Null while it is a draft.
    public Guid? ApprovalInstanceId { get; set; }

    // The vacancy it became. Set once, and the reason this record can be
    // read as "authorised, and here is what came of it".
    public Guid? RequisitionId { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public string? CancellationReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum WorkforceRefusal
{
    None,

    PositionRequired,
    JustificationRequired,
    HiresNotPositive,

    // Finance books the spend against a cost centre. Without one there is
    // nothing to charge, and an approver would be authorising an amount
    // against nothing.
    CostCentreRequired,

    // Already with approvers.
    AlreadySubmitted,

    // Only a draft or a returned request can be sent.
    NotSubmittable,

    // Converting something nobody has approved.
    NotApproved,

    AlreadyConverted,

    // Editing or cancelling after the vacancy exists. The vacancy is the
    // record from then on.
    AlreadyClosed,

    // A vacancy needs to know which department it belongs to.
    DepartmentRequired,
}

public static class WorkforceRequests
{
    // What the request is, as opposed to what somebody typed into it.
    //
    // Derived rather than stored, so a request cannot read "pending" after
    // its chain was approved. The frontend keeps a status column and a note
    // saying the interesting values come from the approval instead — this is
    // that note, enforced.
    public static WorkforceRequestStatus Derive(
        WorkforceRequest request,
        ApprovalStatus? approval)
    {
        if (request.RequisitionId is not null)
        {
            return WorkforceRequestStatus.Converted;
        }

        if (request.CancelledAt is not null)
        {
            return WorkforceRequestStatus.Cancelled;
        }

        return approval switch
        {
            null or ApprovalStatus.Draft => WorkforceRequestStatus.Draft,
            ApprovalStatus.InProgress => WorkforceRequestStatus.PendingApproval,
            ApprovalStatus.Approved => WorkforceRequestStatus.Approved,
            ApprovalStatus.Rejected => WorkforceRequestStatus.Rejected,
            ApprovalStatus.Returned => WorkforceRequestStatus.Returned,

            // A cancelled chain leaves the request editable again rather than
            // stranded: somebody withdrew the approval, not the request.
            _ => WorkforceRequestStatus.Draft,
        };
    }

    // Enough to put in front of an approver. Checked on submission rather
    // than on save, so a half-written draft can be kept.
    public static WorkforceRefusal CanSubmit(
        WorkforceRequest request,
        ApprovalStatus? approval)
    {
        WorkforceRequestStatus status = Derive(request, approval);

        if (status is WorkforceRequestStatus.Converted or WorkforceRequestStatus.Cancelled)
        {
            return WorkforceRefusal.AlreadyClosed;
        }

        if (status == WorkforceRequestStatus.PendingApproval)
        {
            return WorkforceRefusal.AlreadySubmitted;
        }

        if (status == WorkforceRequestStatus.Approved)
        {
            return WorkforceRefusal.NotSubmittable;
        }

        if (string.IsNullOrWhiteSpace(request.Position))
        {
            return WorkforceRefusal.PositionRequired;
        }

        if (string.IsNullOrWhiteSpace(request.Justification))
        {
            return WorkforceRefusal.JustificationRequired;
        }

        if (request.NumberOfHires < 1)
        {
            return WorkforceRefusal.HiresNotPositive;
        }

        return string.IsNullOrWhiteSpace(request.CostCentreCode)
            ? WorkforceRefusal.CostCentreRequired
            : WorkforceRefusal.None;
    }

    public static WorkforceRefusal CanConvert(
        WorkforceRequest request,
        ApprovalStatus? approval)
    {
        if (request.RequisitionId is not null)
        {
            return WorkforceRefusal.AlreadyConverted;
        }

        if (Derive(request, approval) != WorkforceRequestStatus.Approved)
        {
            return WorkforceRefusal.NotApproved;
        }

        return request.DepartmentId is null
            ? WorkforceRefusal.DepartmentRequired
            : WorkforceRefusal.None;
    }

    // A request stops being editable once a vacancy exists for it, or once it
    // is with approvers — an approver reading a request that changes under
    // them is approving something nobody saw.
    public static WorkforceRefusal CanEdit(
        WorkforceRequest request,
        ApprovalStatus? approval) => Derive(request, approval) switch
        {
            WorkforceRequestStatus.Draft or WorkforceRequestStatus.Returned
                or WorkforceRequestStatus.Rejected => WorkforceRefusal.None,
            WorkforceRequestStatus.PendingApproval => WorkforceRefusal.AlreadySubmitted,
            _ => WorkforceRefusal.AlreadyClosed,
        };

    public static WorkforceRefusal CanCancel(
        WorkforceRequest request,
        ApprovalStatus? approval) =>
        Derive(request, approval) is WorkforceRequestStatus.Converted
            or WorkforceRequestStatus.Cancelled
            ? WorkforceRefusal.AlreadyClosed
            : WorkforceRefusal.None;

    // The contract shape a vacancy inherits from the request, so the two
    // cannot disagree about what is being recruited.
    public static EmploymentType EmploymentTypeOf(VacancyType vacancy) => vacancy switch
    {
        VacancyType.Contract => EmploymentType.Contract,
        VacancyType.Temporary => EmploymentType.Temporary,
        VacancyType.FixedTerm => EmploymentType.Contract,
        VacancyType.Apprentice => EmploymentType.FullTime,
        VacancyType.Graduate => EmploymentType.FullTime,
        _ => EmploymentType.FullTime,
    };

    public static string Reference(int year, int sequence) =>
        $"WFR-{year}-{sequence:D5}";
}
