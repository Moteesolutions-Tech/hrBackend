using Motee.Domain.Common;

namespace Motee.Domain.Organisation;

public enum PayFrequency
{
    Weekly,
    BiWeekly,
    SemiMonthly,
    Monthly,
}

public enum ContractDuration
{
    Permanent,
    Fixed1Year,
    Fixed2Years,
    Fixed3Years,
    Contract3Months,
    Contract6Months,
}

// What a kind of employment means at this company.
//
// The eleven built-in kinds are seeded per tenant and are theirs to configure;
// a tenant may add their own beside them. Nothing here is inferred from the
// name — the frontend reads "Part-time" out of the label to decide twenty
// hours a week, and "contract" to decide fortnightly pay, so renaming a type
// or writing it "Part time" silently changes the figures it drives.
public class EmploymentTypeConfig : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Which built-in kind this configures. Null for a type the tenant
    // invented, which is what lets employees keep pointing at a built-in kind
    // after it has been renamed.
    public EmploymentType? Kind { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public PayFrequency PayFrequency { get; set; } = PayFrequency.Monthly;

    public ContractDuration ContractDuration { get; set; } = ContractDuration.Permanent;

    // Null where nobody has set an entitlement, which is not an entitlement
    // of none — both are legitimate and they mean different things.
    public int? DefaultLeaveDays { get; set; }

    public int ProbationMonths { get; set; }

    public bool ProbationReviewRequired { get; set; }

    // Null for a casual or seasonal arrangement with no fixed week. Zero
    // would read as "contracted for no hours", which is a real and different
    // arrangement.
    public decimal? HoursPerWeek { get; set; }

    public bool FlexibleHours { get; set; }

    public bool PayrollInclusion { get; set; } = true;

    public bool EligibleForBenefits { get; set; }

    public decimal? PensionEmployeePercent { get; set; }

    public decimal? PensionEmployerPercent { get; set; }

    // What this type is entitled to — HMO, transport allowance. Read with the
    // row and never queried across.
    public List<string> Benefits { get; set; } = [];

    // Overrides the country's statutory set where a company has to. Empty
    // means "whatever the country says", which is the answer that stays right
    // when the law changes.
    public List<string> StatutoryDeductionOverrides { get; set; } = [];

    // Deactivated rather than deleted, because employees point at it and
    // history has to keep reading correctly.
    public bool IsActive { get; set; } = true;

    public int Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum EmploymentTypeRefusal
{
    None,
    NameRequired,
    LeaveDaysNegative,
    ProbationNegative,
    ProbationTooLong,
    HoursOutOfRange,
    PensionOutOfRange,

    // Two types with one name is two answers to "which is Sarah on".
    NameTaken,

    // Deactivating or deleting a type people are employed under.
    InUse,

    // The eleven the product recognises stay, because the rest of the system
    // maps onto them.
    BuiltInCannotBeDeleted,
}

public static class EmploymentTypeConfigs
{
    // Two years. Long enough for the longest probation anybody runs, short
    // enough to catch a typed "60" that meant months and means five years.
    private const int LongestProbationMonths = 24;

    private const decimal LongestWeek = 168m;

    public static EmploymentTypeRefusal Refusal(EmploymentTypeConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            return EmploymentTypeRefusal.NameRequired;
        }

        if (config.DefaultLeaveDays is int days && days < 0)
        {
            return EmploymentTypeRefusal.LeaveDaysNegative;
        }

        if (config.ProbationMonths < 0)
        {
            return EmploymentTypeRefusal.ProbationNegative;
        }

        if (config.ProbationMonths > LongestProbationMonths)
        {
            return EmploymentTypeRefusal.ProbationTooLong;
        }

        if (config.HoursPerWeek is decimal hours && (hours < 0m || hours > LongestWeek))
        {
            return EmploymentTypeRefusal.HoursOutOfRange;
        }

        return OutOfRange(config.PensionEmployeePercent)
            || OutOfRange(config.PensionEmployerPercent)
            ? EmploymentTypeRefusal.PensionOutOfRange
            : EmploymentTypeRefusal.None;
    }

    private static bool OutOfRange(decimal? percent) =>
        percent is decimal value && (value < 0m || value > 100m);

    // What actually comes off the pay, which is the override where a company
    // has set one and the country's own set otherwise.
    public static IReadOnlyList<string> DeductionsFor(
        EmploymentTypeConfig config,
        CountryCode country) =>
        config.StatutoryDeductionOverrides.Count > 0
            ? config.StatutoryDeductionOverrides
            : StatutoryDeductions.For(country, config.EligibleForBenefits);
}

// What the law takes, by jurisdiction.
//
// Held here for the same reason the public holiday calendar is: it is
// statutory rather than configurable, and a company that edits it has
// usually misunderstood rather than found an exception. Overridable per type
// all the same, because there are real exceptions.
public static class StatutoryDeductions
{
    // Nigeria, per the client's payroll spec. PAYE and NHF come off the
    // employee, pension is both sides, and NSITF and ITF are the employer's
    // for qualifying organisations.
    private static readonly string[] NigeriaFull = ["PAYE", "Pension", "NHF", "NSITF", "ITF"];

    private static readonly string[] UnitedKingdomFull = ["PAYE", "Pension"];

    // Income tax applies whoever you are. Pension does not — deducting for a
    // scheme somebody is not in is a real payroll error, not a rounding one.
    private static readonly string[] TaxOnly = ["PAYE"];

    public static IReadOnlyList<string> For(CountryCode country, bool eligibleForBenefits)
    {
        if (!eligibleForBenefits)
        {
            return TaxOnly;
        }

        return country == CountryCode.Nigeria ? NigeriaFull : UnitedKingdomFull;
    }
}

public sealed record EmploymentTypeSeed
{
    public required EmploymentType Kind { get; init; }

    public required string Name { get; init; }

    // From the HR spec, so a reader choosing between "casual" and "temporary"
    // has something to go on.
    public required string Description { get; init; }

    public required PayFrequency PayFrequency { get; init; }

    public required ContractDuration ContractDuration { get; init; }

    public decimal? HoursPerWeek { get; init; }

    public int ProbationMonths { get; init; }

    public required bool EligibleForBenefits { get; init; }
}

// The eleven kinds the product recognises, matching the frontend's canonical
// list exactly.
//
// Seeded per tenant and theirs to change afterwards. Hours and pay frequency
// come from the kind rather than from the label, which is the difference
// between configuration and a string match that breaks on a rename.
//
// Pension percentages are deliberately absent: 8% and 10% are Nigeria's
// statutory minimum and not every company's actual scheme, and a seeded
// number that looks configured is worse than a blank somebody has to fill in.
public static class EmploymentTypeSeeds
{
    public static readonly IReadOnlyList<EmploymentTypeSeed> All =
    [
        new()
        {
            Kind = EmploymentType.FullTime,
            Name = "Full-time",
            Description = "Regular hours, typically 35–40 a week, with full benefits.",
            PayFrequency = PayFrequency.Monthly,
            ContractDuration = ContractDuration.Permanent,
            HoursPerWeek = 40m,
            ProbationMonths = 6,
            EligibleForBenefits = true,
        },
        new()
        {
            Kind = EmploymentType.PartTime,
            Name = "Part-time",
            Description = "Fewer hours than full-time, with benefits pro-rated.",
            PayFrequency = PayFrequency.Monthly,
            ContractDuration = ContractDuration.Permanent,
            HoursPerWeek = 20m,
            ProbationMonths = 6,
            EligibleForBenefits = true,
        },
        new()
        {
            Kind = EmploymentType.Temporary,
            Name = "Temporary",
            Description = "Engaged for a short, defined stretch of work.",
            PayFrequency = PayFrequency.BiWeekly,
            ContractDuration = ContractDuration.Contract6Months,
            HoursPerWeek = 40m,
            EligibleForBenefits = false,
        },
        new()
        {
            Kind = EmploymentType.Contract,
            Name = "Contract",
            Description = "Engaged under a contract for service for a fixed term.",
            PayFrequency = PayFrequency.BiWeekly,
            ContractDuration = ContractDuration.Contract6Months,
            HoursPerWeek = 40m,
            EligibleForBenefits = false,
        },
        new()
        {
            Kind = EmploymentType.Freelance,
            Name = "Freelance / Self-employed",
            Description = "Self-employed, invoicing for work rather than on payroll.",
            PayFrequency = PayFrequency.Monthly,
            ContractDuration = ContractDuration.Permanent,
            EligibleForBenefits = false,
        },
        new()
        {
            Kind = EmploymentType.Internship,
            Name = "Internship",
            Description = "A placement with a learning purpose and a fixed end.",
            PayFrequency = PayFrequency.BiWeekly,
            ContractDuration = ContractDuration.Contract3Months,
            HoursPerWeek = 40m,
            EligibleForBenefits = false,
        },
        new()
        {
            Kind = EmploymentType.Apprenticeship,
            Name = "Apprenticeship",
            Description = "Structured training alongside paid work towards a qualification.",
            PayFrequency = PayFrequency.Monthly,
            ContractDuration = ContractDuration.Fixed1Year,
            HoursPerWeek = 40m,
            ProbationMonths = 3,
            EligibleForBenefits = true,
        },
        new()
        {
            Kind = EmploymentType.Casual,
            Name = "Casual",
            Description = "No guaranteed hours; worked as and when offered and accepted.",
            PayFrequency = PayFrequency.Weekly,
            ContractDuration = ContractDuration.Permanent,
            EligibleForBenefits = false,
        },
        new()
        {
            Kind = EmploymentType.Seasonal,
            Name = "Seasonal",
            Description = "Recurring work tied to a season or peak period.",
            PayFrequency = PayFrequency.Weekly,
            ContractDuration = ContractDuration.Contract3Months,
            EligibleForBenefits = false,
        },
        new()
        {
            Kind = EmploymentType.Remote,
            Name = "Remote / Telecommuting",
            Description = "Full-time terms, worked away from a company site.",
            PayFrequency = PayFrequency.Monthly,
            ContractDuration = ContractDuration.Permanent,
            HoursPerWeek = 40m,
            ProbationMonths = 6,
            EligibleForBenefits = true,
        },
        new()
        {
            Kind = EmploymentType.FieldBased,
            Name = "Field-based",
            Description = "Based away from an office, working at client or site locations.",
            PayFrequency = PayFrequency.Monthly,
            ContractDuration = ContractDuration.Permanent,
            HoursPerWeek = 40m,
            ProbationMonths = 6,
            EligibleForBenefits = true,
        },
    ];
}
