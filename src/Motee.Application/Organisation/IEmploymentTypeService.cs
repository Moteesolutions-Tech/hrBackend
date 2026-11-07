using Motee.Domain.Organisation;

namespace Motee.Application.Organisation;

// Employment types, and what each one means at this company.
//
// The eleven kinds the product recognises are seeded per tenant and are
// theirs to configure; they may add their own beside them. What cannot happen
// is removing a kind the rest of the system maps onto, or retiring a type
// people are currently employed under.
public interface IEmploymentTypeService
{
    // Every type, with how many people are on each. Inactive ones included,
    // because the screen that reactivates them has to be able to see them.
    Task<IReadOnlyList<EmploymentTypeDto>> ListAsync(
        bool includeInactive = true,
        CancellationToken cancellationToken = default);

    Task<EmploymentTypeDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<EmploymentTypeResult> CreateAsync(
        EmploymentTypeRequest request,
        CancellationToken cancellationToken = default);

    Task<EmploymentTypeResult> UpdateAsync(
        Guid id,
        EmploymentTypeRequest request,
        CancellationToken cancellationToken = default);

    // Retire a type without losing the history pointing at it. Refused while
    // anybody is employed under it: deactivating would leave their record
    // reading against something the company says it no longer uses.
    Task<EmploymentTypeResult> DeactivateAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<EmploymentTypeResult> ReactivateAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // Only ever a tenant's own, unused type. The built-in eleven stay.
    Task<EmploymentTypeOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed record EmploymentTypeDto
{
    public required Guid Id { get; init; }

    // Which built-in kind this configures, or null for one the tenant added.
    public EmploymentType? Kind { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required PayFrequency PayFrequency { get; init; }

    public required ContractDuration ContractDuration { get; init; }

    // Null where nobody has set one, which is not an entitlement of none.
    public int? DefaultLeaveDays { get; init; }

    public required int ProbationMonths { get; init; }

    public required bool ProbationReviewRequired { get; init; }

    // Null for a casual or seasonal arrangement with no fixed week.
    public decimal? HoursPerWeek { get; init; }

    public required bool FlexibleHours { get; init; }

    public required bool PayrollInclusion { get; init; }

    public required bool EligibleForBenefits { get; init; }

    public decimal? PensionEmployeePercent { get; init; }

    public decimal? PensionEmployerPercent { get; init; }

    public required IReadOnlyList<string> Benefits { get; init; }

    // What actually comes off the pay: the company's override where it set
    // one, and the country's statutory set otherwise.
    public required IReadOnlyList<string> StatutoryDeductions { get; init; }

    // Whether the set above is this company's own decision or the law's, so
    // the screen can say which it is showing.
    public required bool DeductionsOverridden { get; init; }

    public required bool IsActive { get; init; }

    // Derived. The number that decides whether a type can be retired.
    public required int EmployeeCount { get; init; }

    // A built-in kind cannot be deleted, so the screen can withhold the
    // button rather than offer one that fails.
    public required bool CanDelete { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record EmploymentTypeRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public PayFrequency PayFrequency { get; init; } = PayFrequency.Monthly;

    public ContractDuration ContractDuration { get; init; } = ContractDuration.Permanent;

    public int? DefaultLeaveDays { get; init; }

    public int ProbationMonths { get; init; }

    public bool ProbationReviewRequired { get; init; }

    public decimal? HoursPerWeek { get; init; }

    public bool FlexibleHours { get; init; }

    public bool PayrollInclusion { get; init; } = true;

    public bool EligibleForBenefits { get; init; }

    public decimal? PensionEmployeePercent { get; init; }

    public decimal? PensionEmployerPercent { get; init; }

    public IReadOnlyList<string>? Benefits { get; init; }

    // Empty leaves the country's statutory set in force, which is the answer
    // that stays right when the law changes.
    public IReadOnlyList<string>? StatutoryDeductionOverrides { get; init; }
}

public enum EmploymentTypeOutcome
{
    Succeeded,
    NotFound,
    Refused,
}

public sealed record EmploymentTypeResult
{
    public required EmploymentTypeOutcome Outcome { get; init; }

    public EmploymentTypeDto? Type { get; init; }

    public EmploymentTypeRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == EmploymentTypeOutcome.Succeeded;

    public static EmploymentTypeResult Failed(EmploymentTypeOutcome outcome) =>
        new() { Outcome = outcome };

    public static EmploymentTypeResult Refused(EmploymentTypeRefusal refusal) =>
        new() { Outcome = EmploymentTypeOutcome.Refused, Refusal = refusal };

    public static EmploymentTypeResult Ok(EmploymentTypeDto type) =>
        new() { Outcome = EmploymentTypeOutcome.Succeeded, Type = type };
}
