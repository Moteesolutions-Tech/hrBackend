using Motee.Application.Common;
using Motee.Domain.Workforce;

namespace Motee.Application.Workforce;

// Workforce requests: a manager asking for headcount.
//
// The first link in the chain the product is built around — a gap on the
// headcount plan becomes a request, the request goes through the approval
// engine, an approved request becomes a vacancy, the vacancy takes
// candidates, and a hire becomes an employee. Every other link already
// exists; without this one the chain starts at the vacancy and nobody can
// answer who authorised it or against which budget.
//
// Nothing here implements approval. It starts a chain through the existing
// engine and reads the answer back, so a tenant's configured approval route
// applies to headcount the same way it applies to expenses.
public interface IWorkforceRequestService
{
    Task<PagedResult<WorkforceRequestDto>> ListAsync(
        WorkforceRequestQuery query,
        CancellationToken cancellationToken = default);

    Task<WorkforceRequestDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<WorkforceRequestResult> CreateAsync(
        WorkforceRequestRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkforceRequestResult> UpdateAsync(
        Guid id,
        WorkforceRequestRequest request,
        CancellationToken cancellationToken = default);

    // Hands it to the approval engine and issues its reference. Resubmits a
    // request that came back, which is why it is not called Submit-once.
    Task<WorkforceRequestResult> SubmitAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<WorkforceRequestResult> CancelAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default);

    // Turns an approved request into a vacancy, carrying the position, the
    // department, the contract shape, the openings and the start date across
    // so the two cannot disagree about what is being recruited.
    Task<WorkforceRequestResult> ConvertToRequisitionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<WorkforceRequestOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed record WorkforceRequestDto
{
    public required Guid Id { get; init; }

    // WFR-2026-00124. Null until submitted — a draft has nothing to quote.
    public string? Reference { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? DepartmentName { get; init; }

    public Guid? BusinessUnitId { get; init; }

    public required string Position { get; init; }

    public string? Grade { get; init; }

    public required int NumberOfHires { get; init; }

    public required HiringReason HiringReason { get; init; }

    public required VacancyType VacancyType { get; init; }

    public required WorkforceUrgency Urgency { get; init; }

    public required string Justification { get; init; }

    public decimal? BudgetEstimate { get; init; }

    public string? Currency { get; init; }

    public string? CostCentreCode { get; init; }

    public DateOnly? ExpectedStartDate { get; init; }

    public Guid? RequestedByEmployeeId { get; init; }

    public string? RequestedByName { get; init; }

    // Re-read from the approval chain every time, so it cannot say "pending"
    // over a chain that was approved last week.
    public required WorkforceRequestStatus Status { get; init; }

    public Guid? ApprovalInstanceId { get; init; }

    // Who it is waiting on, in words, so the list can say so without the
    // caller having to fetch the chain.
    public string? WaitingOn { get; init; }

    public Guid? RequisitionId { get; init; }

    // The vacancy's own reference, once there is one. Closes the loop for
    // somebody reading the request and asking what came of it.
    public string? RequisitionReference { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public string? CancellationReason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record WorkforceRequestQuery : PagedQuery
{
    public Guid? DepartmentId { get; init; }

    public WorkforceRequestStatus? Status { get; init; }

    public Guid? RequestedByEmployeeId { get; init; }

    public WorkforceUrgency? Urgency { get; init; }

    public string? Search { get; init; }
}

public sealed record WorkforceRequestRequest
{
    public Guid? DepartmentId { get; init; }

    public Guid? BusinessUnitId { get; init; }

    public required string Position { get; init; }

    public string? Grade { get; init; }

    public int NumberOfHires { get; init; } = 1;

    public HiringReason HiringReason { get; init; } = HiringReason.NewPosition;

    public VacancyType VacancyType { get; init; } = VacancyType.Permanent;

    public WorkforceUrgency Urgency { get; init; } = WorkforceUrgency.Medium;

    public required string Justification { get; init; }

    public decimal? BudgetEstimate { get; init; }

    public string? Currency { get; init; }

    public string? CostCentreCode { get; init; }

    public DateOnly? ExpectedStartDate { get; init; }
}

public enum WorkforceRequestOutcome
{
    Succeeded,
    NotFound,
    DepartmentNotFound,

    // Broke a domain rule; Refusal says which.
    Refused,

    // The approval engine would not start a chain — usually no template is
    // configured for workforce requests in this tenant.
    CouldNotStartApproval,

    // The vacancy could not be created from the approved request.
    CouldNotCreateRequisition,

    ReasonRequired,
}

public sealed record WorkforceRequestResult
{
    public required WorkforceRequestOutcome Outcome { get; init; }

    public WorkforceRequestDto? Request { get; init; }

    public WorkforceRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == WorkforceRequestOutcome.Succeeded;

    public static WorkforceRequestResult Failed(WorkforceRequestOutcome outcome) =>
        new() { Outcome = outcome };

    public static WorkforceRequestResult Refused(WorkforceRefusal refusal) =>
        new() { Outcome = WorkforceRequestOutcome.Refused, Refusal = refusal };

    public static WorkforceRequestResult Ok(WorkforceRequestDto request) =>
        new() { Outcome = WorkforceRequestOutcome.Succeeded, Request = request };
}
