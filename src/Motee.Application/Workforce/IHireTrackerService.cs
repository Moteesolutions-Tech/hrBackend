using Motee.Domain.Workforce;

namespace Motee.Application.Workforce;

// The hire tracker: every hiring effort in the company, on one line each.
//
// Owns no data. It joins the five records that make up a hire — the headcount
// request, the vacancy, the candidates, their offers and the onboarding — and
// answers the one question none of those modules can answer on its own: how
// far along is this, and has it moved lately.
//
// That question is why the module exists. A vacancy module shows vacancies and
// an onboarding module shows joiners, and neither shows the role that was
// approved in August, advertised in September and has had nobody look at it
// since.
public interface IHireTrackerService
{
    Task<IReadOnlyList<HireTrackerRowDto>> ListAsync(
        HireTrackerQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record HireTrackerQuery
{
    public Guid? DepartmentId { get; init; }

    public HiringStage? Stage { get; init; }

    // Only the efforts that have not moved inside the stall window.
    public bool? Stalled { get; init; }

    public string? Search { get; init; }
}

public sealed record HireTrackerRowDto
{
    // The furthest-along record in the chain, so the row has a stable key and
    // the screen knows what to open.
    public required string Id { get; init; }

    public Guid? WorkforceRequestId { get; init; }

    public Guid? RequisitionId { get; init; }

    public required string Title { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? DepartmentName { get; init; }

    public required HiringStage Stage { get; init; }

    // 1–6, so progress renders without a lookup.
    public required int Step { get; init; }

    // What is happening right now, in words. A stage name alone does not tell
    // somebody what to do next.
    public required string Detail { get; init; }

    // The named hire, once there is one.
    public string? Person { get; init; }

    public Guid? HiringManagerId { get; init; }

    public string? HiringManagerName { get; init; }

    // Days since anything on this effort changed.
    public required int DaysSinceMoved { get; init; }

    public required bool Stalled { get; init; }

    public required int Applicants { get; init; }

    public required int InSelection { get; init; }

    public required int Openings { get; init; }
}
