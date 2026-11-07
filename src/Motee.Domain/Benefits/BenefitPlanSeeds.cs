using Motee.Domain.Common;
using Motee.Domain.Organisation;

namespace Motee.Domain.Benefits;

public sealed record BenefitPlanSeed
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required BenefitCategory Category { get; init; }

    public required string Description { get; init; }

    public required BenefitScope Scope { get; init; }

    public IReadOnlyList<EmploymentType> EmploymentTypes { get; init; } = [];

    public required BenefitEnrollment Enrollment { get; init; }

    // Null where the benefit is the same wherever the company operates.
    public CountryCode? Country { get; init; }
}

// The benefits catalogue a new tenant starts with, so "set up benefits" does
// not begin from a blank page.
//
// Every plan is seeded as a draft, and deliberately carries no provider, no
// contribution rate, no waiting period and no coverage level. Those are facts
// about contracts this company may never have signed, and a seeded figure that
// looks configured is worse than a blank somebody has to fill in — nobody
// checks a number that is already there. An HMO row that arrives active, at
// 90 days' waiting and naming an insurer, tells a new joiner they have cover
// the company has not bought.
//
// So what is seeded is the shape: what the benefit is, who it reaches, and
// whether it lands automatically. HR fills in the terms and activates what
// they actually offer, and the Draft tab is the list of what still needs a
// decision.
public static class BenefitPlanSeeds
{
    private static readonly EmploymentType[] Regular =
        [EmploymentType.FullTime, EmploymentType.Remote, EmploymentType.FieldBased];

    private static readonly EmploymentType[] RegularPlusContract =
        [.. Regular, EmploymentType.Contract];

    private static readonly EmploymentType[] RegularPlusPartTime =
        [.. Regular, EmploymentType.PartTime];

    // Everyone on the payroll under an arrangement with agreed hours — which
    // leaves out freelance, internship, apprenticeship and casual work.
    private static readonly EmploymentType[] Broad =
    [
        .. Regular,
        EmploymentType.Contract,
        EmploymentType.Temporary,
        EmploymentType.Seasonal,
        EmploymentType.PartTime,
    ];

    // The three levels the plan form offers out of the box. The tenant renames
    // and adds to them from the Manage dialog.
    public static readonly IReadOnlyList<string> CoverageLevels =
        ["Employee Only", "Married Cover", "Family Cover"];

    public static readonly IReadOnlyList<BenefitPlanSeed> All =
    [
        // ── wherever the company operates ───────────────────────────────────
        new()
        {
            Key = "annual-leave",
            Name = "Annual Leave",
            Category = BenefitCategory.Leave,
            Description =
                "Paid time off every year. The entitlement itself is set per employment "
                + "type and leave policy; this is the catalogue entry employees read.",
            Scope = BenefitScope.Everyone,
            Enrollment = BenefitEnrollment.Core,
        },
        new()
        {
            Key = "parental-leave",
            Name = "Paid Parental Leave",
            Category = BenefitCategory.Leave,
            Description = "Paid time off around the birth or adoption of a child.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Core,
        },
        new()
        {
            Key = "remote-stipend",
            Name = "Remote Work & Data Stipend",
            Category = BenefitCategory.Perks,
            Description =
                "A stipend towards internet and home-office costs for staff who work "
                + "away from a company site.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = [EmploymentType.FullTime, EmploymentType.Remote, EmploymentType.FieldBased],
            Enrollment = BenefitEnrollment.Core,
        },
        new()
        {
            Key = "employee-assistance",
            Name = "Employee Assistance Programme",
            Category = BenefitCategory.Wellness,
            Description =
                "Confidential counselling and mental health support, open to employees "
                + "and their immediate family.",
            Scope = BenefitScope.Everyone,
            Enrollment = BenefitEnrollment.Core,
        },
        new()
        {
            Key = "wellness-subsidy",
            Name = "Wellness & Gym Membership Subsidy",
            Category = BenefitCategory.Wellness,
            Description = "A contribution towards gym or fitness membership fees.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = RegularPlusPartTime,
            Enrollment = BenefitEnrollment.Optional,
        },
        new()
        {
            Key = "learning-budget",
            Name = "Learning & Development Budget",
            Category = BenefitCategory.Development,
            Description =
                "A budget towards courses, certifications and conferences, alongside "
                + "internal training.",
            Scope = BenefitScope.Everyone,
            Enrollment = BenefitEnrollment.Optional,
        },
        new()
        {
            Key = "long-service-award",
            Name = "Long Service Award",
            Category = BenefitCategory.Financial,
            Description = "Recognition and an award at long-service work anniversaries.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Core,
        },

        // ── Nigeria ─────────────────────────────────────────────────────────
        new()
        {
            Key = "ng-hmo",
            Name = "Group Health Insurance (HMO)",
            Category = BenefitCategory.Health,
            Description =
                "In-patient and out-patient medical cover through a licensed HMO, with "
                + "the option to extend to immediate family.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = RegularPlusContract,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-pension",
            Name = "Contributory Pension Scheme",
            Category = BenefitCategory.Retirement,
            Description =
                "A retirement savings account with a PenCom-licensed PFA, under the "
                + "Pension Reform Act. The statutory minimum is 10% employer and 8% "
                + "employee of monthly emoluments — set the company's own split before "
                + "activating.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-group-life",
            Name = "Group Life Assurance",
            Category = BenefitCategory.Insurance,
            Description =
                "Life cover payable to a nominated beneficiary. The Pension Reform Act "
                + "sets the minimum at three times annual total emoluments.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-personal-accident",
            Name = "Group Personal Accident Cover",
            Category = BenefitCategory.Insurance,
            Description =
                "Cover for accidental death, and permanent or temporary disability, "
                + "arising in the course of employment.",
            Scope = BenefitScope.Everyone,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-nhf",
            Name = "National Housing Fund (NHF)",
            Category = BenefitCategory.Retirement,
            Description =
                "Contribution to the Federal Mortgage Bank's National Housing Fund, "
                + "giving access to low-interest mortgage loans. The statutory employee "
                + "contribution is 2.5% of basic salary.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-leave-allowance",
            Name = "Leave Allowance",
            Category = BenefitCategory.Financial,
            Description =
                "A cash allowance paid alongside annual leave — not statutory, but "
                + "standard practice across Nigerian employers.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = RegularPlusContract,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-thirteenth-month",
            Name = "13th Month Bonus",
            Category = BenefitCategory.Financial,
            Description =
                "An additional month's salary paid towards the end of the year. Not a "
                + "statutory requirement, but near-universal across corporate Nigeria.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = RegularPlusContract,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-transport-housing",
            Name = "Transport & Housing Allowance",
            Category = BenefitCategory.Financial,
            Description =
                "A monthly allowance paid alongside salary towards commuting and "
                + "housing costs.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Broad,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-meal-subsidy",
            Name = "Meal Subsidy",
            Category = BenefitCategory.Perks,
            Description =
                "Subsidised meals on site, or a meal allowance for remote and "
                + "field-based staff.",
            Scope = BenefitScope.Everyone,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-staff-loans",
            Name = "Staff Loans",
            Category = BenefitCategory.Financial,
            Description =
                "Loans for rent, school fees, a vehicle or home improvement, repaid by "
                + "monthly instalment. Individual loans are tracked in Employee Loans.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-salary-advance",
            Name = "Salary Advance",
            Category = BenefitCategory.Financial,
            Description =
                "Part of the coming month's salary paid early for an emergency, "
                + "recovered over the following months.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = RegularPlusContract,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-cooperative",
            Name = "Staff Cooperative Scheme",
            Category = BenefitCategory.Financial,
            Description =
                "Voluntary savings through payroll into the staff cooperative, with "
                + "access to cooperative loans at member rates.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Key = "ng-training-sponsorship",
            Name = "Training & Exam Sponsorship",
            Category = BenefitCategory.Development,
            Description =
                "Sponsorship of professional exams and certifications — ICAN, ACCA, "
                + "CIPM, PMP — usually with a service bond on completion.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.Nigeria,
        },

        // ── United Kingdom ──────────────────────────────────────────────────
        new()
        {
            Key = "uk-pension",
            Name = "Workplace Pension (Auto-enrolment)",
            Category = BenefitCategory.Retirement,
            Description =
                "A defined-contribution workplace pension under auto-enrolment, on "
                + "qualifying earnings. The statutory minimum is 8% in total, of which "
                + "at least 3% is the employer's.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Broad,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-pmi",
            Name = "Private Medical Insurance",
            Category = BenefitCategory.Health,
            Description =
                "Private diagnosis and treatment, with the option to add a partner and "
                + "children. Normally a P11D taxable benefit.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-dental",
            Name = "Dental Cover",
            Category = BenefitCategory.Health,
            Description = "Cash-back towards routine and emergency dental treatment.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = RegularPlusPartTime,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-health-screening",
            Name = "Health Screening & Optical Cover",
            Category = BenefitCategory.Health,
            Description =
                "A health assessment and a contribution towards eye tests and glasses.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-life-assurance",
            Name = "Life Assurance",
            Category = BenefitCategory.Insurance,
            Description =
                "A tax-free lump sum to nominated beneficiaries on death in service.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-income-protection",
            Name = "Income Protection",
            Category = BenefitCategory.Insurance,
            Description =
                "Replaces part of salary where long-term illness or injury stops "
                + "somebody working, after a deferred period.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-critical-illness",
            Name = "Critical Illness Cover",
            Category = BenefitCategory.Insurance,
            Description = "A lump sum on diagnosis of a specified serious illness.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-cycle-to-work",
            Name = "Cycle to Work Scheme",
            Category = BenefitCategory.Wellness,
            Description =
                "A bike and safety equipment through salary sacrifice, under the "
                + "government's Cycle to Work scheme.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = RegularPlusPartTime,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-ev-salary-sacrifice",
            Name = "Electric Vehicle Salary Sacrifice",
            Category = BenefitCategory.Perks,
            Description =
                "An electric car leased through salary sacrifice, typically with "
                + "insurance and maintenance included.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-season-ticket-loan",
            Name = "Season Ticket Loan",
            Category = BenefitCategory.Financial,
            Description =
                "An interest-free loan for an annual rail or travel season ticket, "
                + "repaid by monthly instalment.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-employee-loans",
            Name = "Employee Loans & Salary Advance",
            Category = BenefitCategory.Financial,
            Description =
                "Short-term employee loans and advances against earned pay. Individual "
                + "loans are tracked in Employee Loans.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-learning-sponsorship",
            Name = "Learning Sponsorship & Professional Memberships",
            Category = BenefitCategory.Development,
            Description =
                "Study support, certification sponsorship and paid professional body "
                + "memberships — CIPD, ICAEW, BCS and the like.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-flexible-benefits",
            Name = "Flexible Benefits Allowance",
            Category = BenefitCategory.Perks,
            Description =
                "An allowance to spend across the benefits each employee chooses, "
                + "alongside an employee discount platform.",
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = Regular,
            Enrollment = BenefitEnrollment.Optional,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Key = "uk-flexible-working",
            Name = "Hybrid & Flexible Working",
            Category = BenefitCategory.Leave,
            Description =
                "Hybrid working and flexible hours, alongside the statutory right to "
                + "request flexible working.",
            Scope = BenefitScope.Everyone,
            Enrollment = BenefitEnrollment.Core,
            Country = CountryCode.UnitedKingdom,
        },
    ];

    // Only the plans that mean something where the company operates. Seeding
    // the other country's catalogue leaves a Nigerian tenant with fourteen
    // rows about cycle-to-work and salary sacrifice to archive before their
    // own list is readable.
    public static IReadOnlyList<BenefitPlanSeed> For(CountryCode country) =>
        [.. All.Where(seed => seed.Country is null || seed.Country == country)];
}
