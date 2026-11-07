using Motee.Application.Common;
using Motee.Domain.Benefits;
using Motee.Domain.Organisation;

namespace Motee.Application.Benefits;

// The benefits catalogue, and what it resolves to for one person.
//
// Nothing is stored per employee. An entitlement is a plan whose scope reaches
// their employment type, re-derived on every read — so widening a plan from
// full-time staff to everybody reaches everybody the moment it is saved, and
// a company that has recorded nobody's employment type sees that plainly
// instead of seeing a full package.
public interface IBenefitPlanService
{
    // Paged, with the category counts the rail shows alongside. Those counts
    // are over everything the other filters match, before the category filter
    // is applied — otherwise picking a category empties the rail that offers
    // the choice.
    Task<BenefitPlanListDto> ListAsync(
        BenefitPlanQuery query,
        CancellationToken cancellationToken = default);

    Task<BenefitPlanDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // How far the catalogue reaches: how much of it is live, how many people
    // it lands on, and how many it lands on nothing automatic for.
    Task<BenefitSummaryDto> SummaryAsync(CancellationToken cancellationToken = default);

    Task<BenefitPlanResult> CreateAsync(
        BenefitPlanRequest request,
        CancellationToken cancellationToken = default);

    Task<BenefitPlanResult> UpdateAsync(
        Guid id,
        BenefitPlanRequest request,
        CancellationToken cancellationToken = default);

    // A copy, always as a draft. Duplicating an active plan into another
    // active plan would double everybody's entitlement on a misclick.
    Task<BenefitPlanResult> DuplicateAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<BenefitPlanResult> SetStatusAsync(
        Guid id,
        BenefitPlanStatus status,
        CancellationToken cancellationToken = default);

    // Only a plan the company wrote itself. A catalogue plan is archived.
    Task<BenefitPlanResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BenefitCoverageLevelDto>> CoverageLevelsAsync(
        CancellationToken cancellationToken = default);

    Task<BenefitCoverageLevelResult> AddCoverageLevelAsync(
        string name,
        CancellationToken cancellationToken = default);

    // Renaming carries every plan on the level across, because the plan holds
    // a reference rather than a copy of the label.
    Task<BenefitCoverageLevelResult> RenameCoverageLevelAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default);

    // Plans on a removed level are left with no coverage level rather than a
    // label pointing at nothing.
    Task<BenefitPlanOutcome> RemoveCoverageLevelAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // What one person is entitled to, split into what lands automatically and
    // what is theirs to take up, with the waiting period resolved.
    Task<EmployeeBenefitsDto?> ForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);
}

public sealed record BenefitPlanDto
{
    public required Guid Id { get; init; }

    // Which catalogue plan this started as, or null for the company's own.
    public string? SeedKey { get; init; }

    public required string Name { get; init; }

    public required BenefitCategory Category { get; init; }

    public required string Description { get; init; }

    public string? Provider { get; init; }

    public Guid? CoverageLevelId { get; init; }

    // The level's label, resolved for display. Not stored on the plan.
    public string? Coverage { get; init; }

    public string? CoverageDetails { get; init; }

    public decimal? EmployerContributionPercent { get; init; }

    public decimal? EmployeeContributionPercent { get; init; }

    public string? CostNote { get; init; }

    public int? WaitingPeriodDays { get; init; }

    public required BenefitScope Scope { get; init; }

    public required IReadOnlyList<EmploymentType> EmploymentTypes { get; init; }

    public required BenefitPlanStatus Status { get; init; }

    public required BenefitEnrollment Enrollment { get; init; }

    // ISO 3166-1 alpha-2, or null where the benefit applies wherever the
    // company operates. Note the United Kingdom is "GB" here; the frontend's
    // own bundle key is "uk".
    public string? Country { get; init; }

    // A catalogue plan is archived rather than deleted, so the screen can
    // withhold the button instead of offering one that fails.
    public required bool CanDelete { get; init; }

    // Derived: how many employees this plan reaches today. Zero on a draft,
    // because a draft is not an entitlement.
    public required int EligibleEmployees { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record BenefitCategoryCount
{
    public required BenefitCategory Category { get; init; }

    public required int Count { get; init; }
}

public sealed record BenefitPlanListDto
{
    public required PagedResult<BenefitPlanDto> Plans { get; init; }

    // Over the filtered set before the category filter, which is what the
    // rail counts.
    public required IReadOnlyList<BenefitCategoryCount> CategoryCounts { get; init; }
}

public sealed record BenefitSummaryDto
{
    public required int Plans { get; init; }

    public required int Active { get; init; }

    public required int Draft { get; init; }

    public required int Archived { get; init; }

    public required int CoreBenefits { get; init; }

    public required int OptionalBenefits { get; init; }

    // Everybody with a recorded employment type.
    public required int Employees { get; init; }

    // Reported rather than folded into the figures above: counting them as
    // full-time would hand them a package nobody decided they get, and would
    // move the average while it did so.
    public required int EmployeesWithoutEmploymentType { get; init; }

    public required int CoveredEmployees { get; init; }

    public required int EmployeesWithoutCoreCover { get; init; }

    public required decimal AveragePlansPerEmployee { get; init; }

    public required IReadOnlyList<BenefitCategoryCount> ByCategory { get; init; }
}

public sealed record BenefitCoverageLevelDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int Sequence { get; init; }

    // Derived: how many plans are on this level, so the Manage dialog can say
    // what removing it will clear before it clears it.
    public required int PlanCount { get; init; }
}

public sealed record EmployeeBenefitsDto
{
    public required Guid EmployeeId { get; init; }

    // Null where nobody has recorded one, in which case nothing resolves.
    public EmploymentType? EmploymentType { get; init; }

    public required IReadOnlyList<EmployeeBenefitDto> Core { get; init; }

    public required IReadOnlyList<EmployeeBenefitDto> Optional { get; init; }
}

public sealed record EmployeeBenefitDto
{
    public required BenefitPlanDto Plan { get; init; }

    public required BenefitEntitlementState State { get; init; }

    // When the cover starts, where a waiting period applies and the start date
    // is known.
    public DateOnly? EffectiveFrom { get; init; }
}

public sealed record BenefitPlanQuery : PagedQuery
{
    public BenefitPlanStatus? Status { get; init; }

    public BenefitCategory? Category { get; init; }

    public BenefitEnrollment? Enrollment { get; init; }

    public Guid? CoverageLevelId { get; init; }

    // The form's "Not specified" option, which is a filter rather than the
    // absence of one.
    public bool? WithoutCoverageLevel { get; init; }

    public string? Search { get; init; }
}

public sealed record BenefitPlanRequest
{
    public required string Name { get; init; }

    public BenefitCategory Category { get; init; }

    public required string Description { get; init; }

    public string? Provider { get; init; }

    // The level's label, as the form's dropdown offers it. Resolved against
    // the tenant's own levels; an unknown label is refused rather than
    // silently dropped.
    public string? Coverage { get; init; }

    public string? CoverageDetails { get; init; }

    public decimal? EmployerContributionPercent { get; init; }

    public decimal? EmployeeContributionPercent { get; init; }

    public string? CostNote { get; init; }

    public int? WaitingPeriodDays { get; init; }

    public BenefitScope Scope { get; init; }

    public IReadOnlyList<EmploymentType>? EmploymentTypes { get; init; }

    public BenefitPlanStatus Status { get; init; } = BenefitPlanStatus.Draft;

    public BenefitEnrollment Enrollment { get; init; } = BenefitEnrollment.Core;

    // Null means the benefit applies wherever the company operates. Anything
    // other than the company's own country is refused.
    public string? Country { get; init; }
}

public enum BenefitPlanOutcome
{
    Succeeded,
    NotFound,
    Refused,
}

public sealed record BenefitPlanResult
{
    public required BenefitPlanOutcome Outcome { get; init; }

    public BenefitPlanDto? Plan { get; init; }

    public BenefitPlanRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == BenefitPlanOutcome.Succeeded;

    public static BenefitPlanResult Failed(BenefitPlanOutcome outcome) =>
        new() { Outcome = outcome };

    public static BenefitPlanResult Refused(BenefitPlanRefusal refusal) =>
        new() { Outcome = BenefitPlanOutcome.Refused, Refusal = refusal };

    public static BenefitPlanResult Ok(BenefitPlanDto plan) =>
        new() { Outcome = BenefitPlanOutcome.Succeeded, Plan = plan };

    // Deleting leaves nothing to describe, which is still the outcome the
    // caller asked for.
    public static BenefitPlanResult Done() =>
        new() { Outcome = BenefitPlanOutcome.Succeeded };
}

public sealed record BenefitCoverageLevelResult
{
    public required BenefitPlanOutcome Outcome { get; init; }

    public BenefitCoverageLevelDto? Level { get; init; }

    public BenefitPlanRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == BenefitPlanOutcome.Succeeded;

    public static BenefitCoverageLevelResult Failed(BenefitPlanOutcome outcome) =>
        new() { Outcome = outcome };

    public static BenefitCoverageLevelResult Refused(BenefitPlanRefusal refusal) =>
        new() { Outcome = BenefitPlanOutcome.Refused, Refusal = refusal };

    public static BenefitCoverageLevelResult Ok(BenefitCoverageLevelDto level) =>
        new() { Outcome = BenefitPlanOutcome.Succeeded, Level = level };
}
