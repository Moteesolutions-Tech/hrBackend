using Motee.Domain.Common;
using Motee.Domain.Organisation;

namespace Motee.Domain.Benefits;

public enum BenefitCategory
{
    Health,
    Retirement,
    Insurance,
    Leave,
    Financial,
    Wellness,
    Perks,
    Development,
}

// Draft is where a plan starts and where it matters most: it is the catalogue
// before anybody has confirmed the company actually buys it. Only an active
// plan resolves into somebody's entitlement.
public enum BenefitPlanStatus
{
    Draft,
    Active,
    Archived,
}

// Core lands on every eligible employee without anybody asking; optional is
// theirs to take up. The distinction is what makes "nobody in this employment
// type gets anything automatically" a question with an answer.
public enum BenefitEnrollment
{
    Core,
    Optional,
}

public enum BenefitScope
{
    Everyone,
    EmploymentTypes,
}

// A benefit the company offers, and who it reaches.
//
// Entitlement is derived from this plus the employee's employment type, never
// stored against the person: a plan that widens from full-time staff to
// everybody has to reach everybody the moment it is saved, and a stored list
// per employee would still be yesterday's.
public class BenefitPlan : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Which catalogue plan this started as. Null for one the company wrote
    // itself, which is the only kind that can be deleted — something the
    // tenant did not author is archived instead, so the catalogue stays
    // recognisable across tenants.
    public string? SeedKey { get; set; }

    public required string Name { get; set; }

    public BenefitCategory Category { get; set; }

    public required string Description { get; set; }

    // The insurer, administrator or scheme. Null until somebody fills it in —
    // and deliberately null on a seeded plan, because naming a provider the
    // company has not contracted with is a claim, not a default.
    public string? Provider { get; set; }

    // The coverage level, from the tenant's own managed list. A reference
    // rather than the label itself: renaming "Family Cover" has to carry every
    // plan on it across, and a copied string drifts the first time somebody
    // renames one and not the other.
    public Guid? CoverageLevelId { get; set; }

    public string? CoverageDetails { get; set; }

    public decimal? EmployerContributionPercent { get; set; }

    public decimal? EmployeeContributionPercent { get; set; }

    // How it is funded where a contribution split does not apply.
    public string? CostNote { get; set; }

    // Days of service before the cover starts. Null means no waiting period
    // has been recorded, which is not the same as none — hence the separate
    // UnknownStart state when there is a period but no start date to count
    // from.
    public int? WaitingPeriodDays { get; set; }

    public BenefitScope Scope { get; set; }

    // Only meaningful under BenefitScope.EmploymentTypes, and required there.
    // Read with the row and never queried across.
    public List<EmploymentType> EmploymentTypes { get; set; } = [];

    public BenefitPlanStatus Status { get; set; } = BenefitPlanStatus.Draft;

    public BenefitEnrollment Enrollment { get; set; } = BenefitEnrollment.Core;

    // The jurisdiction the plan belongs to, inherited from the catalogue: a
    // Nigerian HMO means nothing to a UK company. Null means it applies
    // wherever the company operates.
    public CountryCode? Country { get; set; }

    public int Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// A coverage level the company offers — "Employee Only", "Family Cover".
//
// A table rather than a free-text field on the plan, because the screen lets
// HR rename and remove levels and both have to reach every plan on them.
public class BenefitCoverageLevel : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public int Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum BenefitPlanRefusal
{
    None,
    NameRequired,
    NameTooLong,
    DescriptionRequired,
    DescriptionTooLong,
    ContributionOutOfRange,
    WaitingPeriodNegative,
    WaitingPeriodTooLong,

    // Restricted to employment types, with none ticked.
    ScopeHasNoEmploymentTypes,

    // A coverage label that is not one of the tenant's own levels.
    CoverageLevelUnknown,

    // A plan for a country this company does not operate in.
    CountryNotThisTenants,

    // A catalogue plan. Archived instead.
    CataloguePlanCannotBeDeleted,

    CoverageLevelNameRequired,
    CoverageLevelNameTaken,
}

public static class BenefitPlans
{
    // The plan form's own limits, so a name the screen accepted is never
    // refused by the API and a name it refused never arrives by another route.
    public const int ShortestName = 2;

    public const int LongestName = 80;

    public const int ShortestDescription = 4;

    public const int LongestDescription = 500;

    // Two years. Longer than any real qualifying period, short enough to catch
    // a figure that was meant to be months.
    public const int LongestWaitingPeriodDays = 730;

    public static BenefitPlanRefusal Refusal(BenefitPlan plan)
    {
        string name = plan.Name?.Trim() ?? string.Empty;
        string description = plan.Description?.Trim() ?? string.Empty;

        if (name.Length < ShortestName)
        {
            return BenefitPlanRefusal.NameRequired;
        }

        if (name.Length > LongestName)
        {
            return BenefitPlanRefusal.NameTooLong;
        }

        if (description.Length < ShortestDescription)
        {
            return BenefitPlanRefusal.DescriptionRequired;
        }

        if (description.Length > LongestDescription)
        {
            return BenefitPlanRefusal.DescriptionTooLong;
        }

        if (OutOfRange(plan.EmployerContributionPercent)
            || OutOfRange(plan.EmployeeContributionPercent))
        {
            return BenefitPlanRefusal.ContributionOutOfRange;
        }

        if (plan.WaitingPeriodDays is int days)
        {
            if (days < 0)
            {
                return BenefitPlanRefusal.WaitingPeriodNegative;
            }

            if (days > LongestWaitingPeriodDays)
            {
                return BenefitPlanRefusal.WaitingPeriodTooLong;
            }
        }

        // The frontend quietly turns this into "every employee", which grants
        // the company's HMO to its interns and freelancers on a forgotten
        // checkbox. Widening entitlement is never the safe fallback.
        return plan.Scope == BenefitScope.EmploymentTypes && plan.EmploymentTypes.Count == 0
            ? BenefitPlanRefusal.ScopeHasNoEmploymentTypes
            : BenefitPlanRefusal.None;
    }

    private static bool OutOfRange(decimal? percent) =>
        percent is decimal value && (value < 0m || value > 100m);

    public static bool AppliesInCountry(BenefitPlan plan, CountryCode country) =>
        plan.Country is null || plan.Country == country;

    public static bool CoversType(BenefitPlan plan, EmploymentType type) =>
        plan.Scope == BenefitScope.Everyone || plan.EmploymentTypes.Contains(type);

    // An entitlement is an active plan whose scope reaches a recorded
    // employment type. Nothing reaches somebody whose type nobody has filled
    // in: the frontend falls back to full-time there, which hands them a
    // package nobody decided they get.
    public static bool IsEligible(BenefitPlan plan, EmploymentType? type) =>
        plan.Status == BenefitPlanStatus.Active
            && type is EmploymentType recorded
            && CoversType(plan, recorded);

    // Null where there is no waiting period to serve, or no start date to
    // count it from — the two are told apart by Entitlements.StateOf.
    public static DateOnly? EffectiveFrom(BenefitPlan plan, DateOnly? startDate) =>
        plan.WaitingPeriodDays is int days && days > 0 && startDate is DateOnly start
            ? start.AddDays(days)
            : null;

    public static bool CanDelete(BenefitPlan plan) => plan.SeedKey is null;
}

public enum BenefitEntitlementState
{
    Entitled,
    Waiting,

    // There is a waiting period but no start date, so whether it has been
    // served is not a question this data can answer.
    UnknownStart,
}

public static class Entitlements
{
    // Saying "entitled" sends somebody to a hospital that will turn them away;
    // saying "waiting" hides cover they may well already have. Where the start
    // date is missing, neither is reported.
    public static BenefitEntitlementState StateOf(
        BenefitPlan plan,
        DateOnly? startDate,
        DateOnly asAt)
    {
        if (plan.WaitingPeriodDays is not int days || days <= 0)
        {
            return BenefitEntitlementState.Entitled;
        }

        if (startDate is not DateOnly start)
        {
            return BenefitEntitlementState.UnknownStart;
        }

        return asAt >= start.AddDays(days)
            ? BenefitEntitlementState.Entitled
            : BenefitEntitlementState.Waiting;
    }
}

// How far the catalogue reaches, which is the figure the hub leads with.
public sealed record BenefitReachTally
{
    // Everybody with a recorded employment type. The rest are reported
    // separately rather than counted as full-time.
    public required int Employees { get; init; }

    public required int WithoutEmploymentType { get; init; }

    public required int Covered { get; init; }

    // Nothing automatic lands on these people. They may still have optional
    // benefits to take up, which is why it is a separate figure from Covered.
    public required int WithoutCoreCover { get; init; }

    public required decimal AveragePlansPerEmployee { get; init; }
}

public static class BenefitReach
{
    // Takes headcount grouped by employment type rather than a list of
    // employees: every plan reaches a type, not a person, so one count per
    // type answers the whole page without walking the roster.
    public static BenefitReachTally Of(
        IReadOnlyList<BenefitPlan> plans,
        IReadOnlyDictionary<EmploymentType, int> headcountByType,
        int withoutEmploymentType)
    {
        List<BenefitPlan> active =
            [.. plans.Where(plan => plan.Status == BenefitPlanStatus.Active)];

        int employees = 0;
        int covered = 0;
        int withoutCore = 0;
        int entitlements = 0;

        foreach ((EmploymentType type, int count) in headcountByType)
        {
            employees += count;

            int reach = active.Count(plan => BenefitPlans.CoversType(plan, type));

            entitlements += reach * count;

            if (reach > 0)
            {
                covered += count;
            }

            bool anyCore = active.Any(plan =>
                plan.Enrollment == BenefitEnrollment.Core && BenefitPlans.CoversType(plan, type));

            if (!anyCore)
            {
                withoutCore += count;
            }
        }

        return new BenefitReachTally
        {
            Employees = employees,
            WithoutEmploymentType = withoutEmploymentType,
            Covered = covered,
            WithoutCoreCover = withoutCore,
            AveragePlansPerEmployee = employees == 0
                ? 0m
                : Math.Round((decimal)entitlements / employees, 1, MidpointRounding.AwayFromZero),
        };
    }
}
