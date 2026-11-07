using Motee.Domain.Common;
using Motee.Domain.Organisation;

namespace Motee.Tests.Unit;

// What a kind of employment means at a company: how often they are paid, how
// long the probation runs, whether pension applies, which statutory
// deductions attach.
//
// The frontend works most of this out from the type's *name* — "Part-time" in
// the label means twenty hours, "contract" means fortnightly pay — and then
// presents the guesses as settings somebody chose. Rename a type, or write it
// "Part time", and every figure silently changes. Stored instead, with the
// statutory part derived from the country rather than from a string.
public class EmploymentTypeConfigTests
{
    private static EmploymentTypeConfig Type(
        string name = "Full-time",
        int? defaultLeaveDays = 25,
        int probationMonths = 6,
        decimal? hoursPerWeek = 40m,
        decimal? pensionEmployee = 8m,
        decimal? pensionEmployer = 10m) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            DefaultLeaveDays = defaultLeaveDays,
            ProbationMonths = probationMonths,
            HoursPerWeek = hoursPerWeek,
            PensionEmployeePercent = pensionEmployee,
            PensionEmployerPercent = pensionEmployer,
        };

    // ── statutory deductions ────────────────────────────────────────────────

    // Nigeria's set, per the client's payroll spec. PAYE and NHF come off the
    // employee, pension is both sides, NSITF and ITF are the employer's.
    [Fact]
    public void NigeriasFullSetAttachesToBenefitEligibleStaff() =>
        Assert.Equal(
            ["PAYE", "Pension", "NHF", "NSITF", "ITF"],
            StatutoryDeductions.For(CountryCode.Nigeria, eligibleForBenefits: true));

    // Somebody not on the benefits scheme still pays income tax. Attaching
    // pension to them would deduct for a scheme they are not in.
    [Fact]
    public void SomebodyOutsideTheSchemeStillPaysIncomeTax() =>
        Assert.Equal(
            ["PAYE"],
            StatutoryDeductions.For(CountryCode.Nigeria, eligibleForBenefits: false));

    [Fact]
    public void TheUkSetIsItsOwn() =>
        Assert.Equal(
            ["PAYE", "Pension"],
            StatutoryDeductions.For(CountryCode.UnitedKingdom, eligibleForBenefits: true));

    [Fact]
    public void TheUkSetNarrowsTheSameWay() =>
        Assert.Equal(
            ["PAYE"],
            StatutoryDeductions.For(CountryCode.UnitedKingdom, eligibleForBenefits: false));

    // ── the built-in taxonomy ───────────────────────────────────────────────

    // The eleven kinds the product recognises, matching the frontend's own
    // canonical list. A tenant may add their own beside them, and may not
    // remove these — a type with employees on it is load-bearing.
    [Fact]
    public void EveryBuiltInKindHasASeedWithANameAndADescription()
    {
        IReadOnlyList<EmploymentTypeSeed> seeds = EmploymentTypeSeeds.All;

        Assert.Equal(Enum.GetValues<EmploymentType>().Length, seeds.Count);

        Assert.All(seeds, seed =>
        {
            Assert.False(string.IsNullOrWhiteSpace(seed.Name));
            Assert.False(string.IsNullOrWhiteSpace(seed.Description));
        });
    }

    [Fact]
    public void TheSeedsCoverEveryKindExactlyOnce() =>
        Assert.Equal(
            [.. Enum.GetValues<EmploymentType>().Order()],
            [.. EmploymentTypeSeeds.All.Select(seed => seed.Kind).Order()]);

    // Set from the kind rather than inferred from the label, which is the
    // whole point: a renamed or translated type keeps its hours.
    [Theory]
    [InlineData(EmploymentType.FullTime, 40)]
    [InlineData(EmploymentType.PartTime, 20)]
    [InlineData(EmploymentType.Casual, null)]
    public void DefaultHoursComeFromTheKindNotTheName(EmploymentType kind, int? expected) =>
        Assert.Equal(
            expected,
            EmploymentTypeSeeds.All.First(seed => seed.Kind == kind).HoursPerWeek);

    // A casual or seasonal worker has no fixed week. Zero would read as
    // "contracted for no hours", which is a different arrangement.
    [Theory]
    [InlineData(EmploymentType.Casual)]
    [InlineData(EmploymentType.Seasonal)]
    public void SomebodyWithNoFixedWeekHasNoHoursRatherThanZero(EmploymentType kind) =>
        Assert.Null(EmploymentTypeSeeds.All.First(seed => seed.Kind == kind).HoursPerWeek);

    [Theory]
    [InlineData(EmploymentType.FullTime, true)]
    [InlineData(EmploymentType.PartTime, true)]
    [InlineData(EmploymentType.Contract, false)]
    [InlineData(EmploymentType.Internship, false)]
    public void BenefitEligibilityIsSetPerKind(EmploymentType kind, bool expected) =>
        Assert.Equal(
            expected,
            EmploymentTypeSeeds.All.First(seed => seed.Kind == kind).EligibleForBenefits);

    // ── validation ──────────────────────────────────────────────────────────

    [Fact]
    public void AConfiguredTypeIsAccepted() =>
        Assert.Equal(EmploymentTypeRefusal.None, EmploymentTypeConfigs.Refusal(Type()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ATypeWithoutANameIsRefused(string name) =>
        Assert.Equal(
            EmploymentTypeRefusal.NameRequired,
            EmploymentTypeConfigs.Refusal(Type(name: name)));

    [Fact]
    public void NegativeLeaveIsRefused() =>
        Assert.Equal(
            EmploymentTypeRefusal.LeaveDaysNegative,
            EmploymentTypeConfigs.Refusal(Type(defaultLeaveDays: -1)));

    // No entitlement recorded is different from an entitlement of none, and
    // both are legitimate.
    [Fact]
    public void NoLeaveEntitlementRecordedIsFine() =>
        Assert.Equal(
            EmploymentTypeRefusal.None,
            EmploymentTypeConfigs.Refusal(Type(defaultLeaveDays: null)));

    [Fact]
    public void ZeroLeaveEntitlementIsFine() =>
        Assert.Equal(
            EmploymentTypeRefusal.None,
            EmploymentTypeConfigs.Refusal(Type(defaultLeaveDays: 0)));

    [Fact]
    public void NegativeProbationIsRefused() =>
        Assert.Equal(
            EmploymentTypeRefusal.ProbationNegative,
            EmploymentTypeConfigs.Refusal(Type(probationMonths: -3)));

    [Fact]
    public void AProbationLongerThanTwoYearsIsRefused() =>
        Assert.Equal(
            EmploymentTypeRefusal.ProbationTooLong,
            EmploymentTypeConfigs.Refusal(Type(probationMonths: 25)));

    [Theory]
    [InlineData(-1)]
    [InlineData(169)]
    public void AnImpossibleWeekIsRefused(decimal hours) =>
        Assert.Equal(
            EmploymentTypeRefusal.HoursOutOfRange,
            EmploymentTypeConfigs.Refusal(Type(hoursPerWeek: hours)));

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void APensionShareOutsideAHundredPercentIsRefused(decimal percent) =>
        Assert.Equal(
            EmploymentTypeRefusal.PensionOutOfRange,
            EmploymentTypeConfigs.Refusal(Type(pensionEmployee: percent)));

    [Fact]
    public void AnEmployersPensionShareIsCheckedToo() =>
        Assert.Equal(
            EmploymentTypeRefusal.PensionOutOfRange,
            EmploymentTypeConfigs.Refusal(Type(pensionEmployer: 140m)));
}
