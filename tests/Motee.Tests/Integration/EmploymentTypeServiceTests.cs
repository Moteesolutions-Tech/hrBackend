using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Employment types and their configuration.
//
// Two things are worth proving. The eleven kinds arrive configured at
// registration, because everything else in the product already carries an
// employment type and a company with none would have screens referring to
// types their own settings page denies exist. And a type people are employed
// under cannot be retired — the count that decides it is derived, so it
// cannot drift out of step with the employees it is counting.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class EmploymentTypeServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 11, 3).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _departmentId;

    private async Task<ServiceProvider> ArrangeAsync(string country = "NG")
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = country,
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId =
            registration.TenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IEmploymentTypeService Types(ServiceProvider provider) =>
        Resolve<IEmploymentTypeService>(provider);

    private async Task HireAsync(
        ServiceProvider provider,
        string first,
        EmploymentType kind) =>
        await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = kind,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        });

    private static EmploymentTypeRequest Configured(
        string name = "Weekend crew",
        int probationMonths = 3,
        decimal? hoursPerWeek = 16m,
        int? leaveDays = 12,
        decimal? pensionEmployee = 8m) => new()
        {
            Name = name,
            Description = "Saturdays and Sundays only.",
            PayFrequency = PayFrequency.Weekly,
            ContractDuration = ContractDuration.Permanent,
            DefaultLeaveDays = leaveDays,
            ProbationMonths = probationMonths,
            HoursPerWeek = hoursPerWeek,
            PensionEmployeePercent = pensionEmployee,
            EligibleForBenefits = true,
            Benefits = ["HMO"],
        };

    private static EmploymentTypeDto KindOf(
        IReadOnlyList<EmploymentTypeDto> types,
        EmploymentType kind) =>
        Assert.Single(types, type => type.Kind == kind);

    // ── what a new company starts with ──────────────────────────────────────

    [SkippableFact]
    public async Task ANewCompanyStartsWithTheElevenKinds()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<EmploymentTypeDto> types = await Types(provider).ListAsync();

        Assert.Equal(Enum.GetValues<EmploymentType>().Length, types.Count);
        Assert.All(types, type => Assert.True(type.IsActive));
        Assert.All(types, type => Assert.NotNull(type.Kind));

        // Each explains itself, so somebody choosing between "casual" and
        // "temporary" has something to go on.
        Assert.All(
            types, type => Assert.False(string.IsNullOrWhiteSpace(type.Description)));
    }

    // Set from the kind, not read out of the label. A renamed or translated
    // type keeps its hours.
    [SkippableFact]
    public async Task HoursComeFromTheKindRatherThanTheName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<EmploymentTypeDto> types = await Types(provider).ListAsync();

        Assert.Equal(40m, KindOf(types, EmploymentType.FullTime).HoursPerWeek);
        Assert.Equal(20m, KindOf(types, EmploymentType.PartTime).HoursPerWeek);

        // No fixed week at all, which is not a week of nothing.
        Assert.Null(KindOf(types, EmploymentType.Casual).HoursPerWeek);
    }

    // A seeded 8% that looks configured is worse than a blank somebody has to
    // fill in — nobody checks a figure that is already there.
    [SkippableFact]
    public async Task PensionPercentagesArriveBlankRatherThanGuessed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        EmploymentTypeDto fullTime = KindOf(
            await Types(provider).ListAsync(), EmploymentType.FullTime);

        Assert.Null(fullTime.PensionEmployeePercent);
        Assert.Null(fullTime.PensionEmployerPercent);
    }

    // ── statutory deductions ────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANigerianCompanyGetsNigeriasDeductions()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<EmploymentTypeDto> types = await Types(provider).ListAsync();

        Assert.Equal(
            ["PAYE", "Pension", "NHF", "NSITF", "ITF"],
            KindOf(types, EmploymentType.FullTime).StatutoryDeductions);

        // Somebody outside the benefits scheme still pays income tax.
        Assert.Equal(["PAYE"], KindOf(types, EmploymentType.Contract).StatutoryDeductions);

        Assert.All(types, type => Assert.False(type.DeductionsOverridden));
    }

    [SkippableFact]
    public async Task AUkCompanyGetsItsOwnSet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("GB");

        Assert.Equal(
            ["PAYE", "Pension"],
            KindOf(await Types(provider).ListAsync(), EmploymentType.FullTime)
                .StatutoryDeductions);
    }

    // Derived on read, so it stays right the next time the law moves — but
    // overridable, because there are real exceptions.
    [SkippableFact]
    public async Task ACompanyCanOverrideWhatComesOffThePay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        EmploymentTypeDto fullTime = KindOf(
            await Types(provider).ListAsync(), EmploymentType.FullTime);

        EmploymentTypeResult updated = await Types(provider).UpdateAsync(
            fullTime.Id,
            Configured(name: fullTime.Name) with
            {
                StatutoryDeductionOverrides = ["PAYE", "Pension"],
            });

        Assert.True(updated.Succeeded, updated.Outcome.ToString());
        Assert.Equal(["PAYE", "Pension"], updated.Type!.StatutoryDeductions);
        Assert.True(updated.Type.DeductionsOverridden);
    }

    // ── the derived count ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task EachTypeReportsHowManyPeopleAreOnIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", EmploymentType.FullTime);
        await HireAsync(provider, "Bola", EmploymentType.FullTime);
        await HireAsync(provider, "Dele", EmploymentType.Contract);

        IReadOnlyList<EmploymentTypeDto> types = await Types(provider).ListAsync();

        Assert.Equal(2, KindOf(types, EmploymentType.FullTime).EmployeeCount);
        Assert.Equal(1, KindOf(types, EmploymentType.Contract).EmployeeCount);
        Assert.Equal(0, KindOf(types, EmploymentType.Casual).EmployeeCount);
    }

    // ── retiring and restoring ──────────────────────────────────────────────

    [SkippableFact]
    public async Task AnUnusedTypeIsRetiredAndRestored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid casual = KindOf(await Types(provider).ListAsync(), EmploymentType.Casual).Id;

        Assert.False((await Types(provider).DeactivateAsync(casual)).Type!.IsActive);
        Assert.True((await Types(provider).ReactivateAsync(casual)).Type!.IsActive);
    }

    // Retiring a type people are on would leave their records reading against
    // something the company says it no longer uses.
    [SkippableFact]
    public async Task ATypePeopleAreEmployedUnderCannotBeRetired()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", EmploymentType.FullTime);

        Guid fullTime = KindOf(await Types(provider).ListAsync(), EmploymentType.FullTime).Id;

        EmploymentTypeResult retired = await Types(provider).DeactivateAsync(fullTime);

        Assert.Equal(EmploymentTypeOutcome.Refused, retired.Outcome);
        Assert.Equal(EmploymentTypeRefusal.InUse, retired.Refusal);
    }

    [SkippableFact]
    public async Task RetiredTypesAreStillListedSoTheyCanBeRestored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid casual = KindOf(await Types(provider).ListAsync(), EmploymentType.Casual).Id;
        await Types(provider).DeactivateAsync(casual);

        Assert.Contains(await Types(provider).ListAsync(), type => type.Id == casual);

        Assert.DoesNotContain(
            await Types(provider).ListAsync(includeInactive: false),
            type => type.Id == casual);
    }

    // ── a company's own types ───────────────────────────────────────────────

    [SkippableFact]
    public async Task ACompanyAddsItsOwnTypeBesideTheBuiltIns()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        EmploymentTypeResult created = await Types(provider).CreateAsync(Configured());

        Assert.True(created.Succeeded, created.Outcome.ToString());
        Assert.Null(created.Type!.Kind);
        Assert.Equal("Weekend crew", created.Type.Name);
        Assert.Equal(16m, created.Type.HoursPerWeek);
        Assert.Equal(["HMO"], created.Type.Benefits);
        Assert.True(created.Type.CanDelete);
    }

    [SkippableFact]
    public async Task ACompanysOwnTypeIsDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Types(provider).CreateAsync(Configured())).Type!.Id;

        Assert.Equal(
            EmploymentTypeOutcome.Succeeded, await Types(provider).DeleteAsync(id));
    }

    // The eleven stay. Recruitment, onboarding and the reports all map onto
    // them, so deleting one breaks screens unrelated to this page.
    [SkippableFact]
    public async Task ABuiltInKindCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid casual = KindOf(await Types(provider).ListAsync(), EmploymentType.Casual).Id;

        Assert.Equal(EmploymentTypeOutcome.Refused, await Types(provider).DeleteAsync(casual));
        Assert.False(KindOf(await Types(provider).ListAsync(), EmploymentType.Casual).CanDelete);
    }

    // Two types with one name is two answers to "which is Sarah on".
    [SkippableFact]
    public async Task TwoTypesCannotShareAName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await Types(provider).CreateAsync(Configured());

        EmploymentTypeResult again = await Types(provider).CreateAsync(
            Configured(name: "weekend CREW"));

        Assert.Equal(EmploymentTypeRefusal.NameTaken, again.Refusal);
    }

    // A name with a wildcard in it is a name, not a pattern.
    [SkippableFact]
    public async Task ANameContainingAPercentSignIsNotAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await Types(provider).CreateAsync(
            Configured(name: "Contract (100%)"))).Succeeded);

        Assert.True((await Types(provider).CreateAsync(
            Configured(name: "Weekend crew"))).Succeeded);
    }

    // ── validation reaches the service ──────────────────────────────────────

    [SkippableFact]
    public async Task AnImpossibleProbationIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            EmploymentTypeRefusal.ProbationTooLong,
            (await Types(provider).CreateAsync(Configured(probationMonths: 60))).Refusal);
    }

    [SkippableFact]
    public async Task APensionOutsideAHundredPercentIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            EmploymentTypeRefusal.PensionOutOfRange,
            (await Types(provider).CreateAsync(Configured(pensionEmployee: 800m))).Refusal);
    }

    [SkippableFact]
    public async Task AnEntitlementNobodyHasSetIsNotZero()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        EmploymentTypeResult created = await Types(provider).CreateAsync(
            Configured(leaveDays: null));

        Assert.True(created.Succeeded, created.Outcome.ToString());
        Assert.Null(created.Type!.DefaultLeaveDays);
    }

    [SkippableFact]
    public async Task ATypeIsReconfigured()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        EmploymentTypeDto partTime = KindOf(
            await Types(provider).ListAsync(), EmploymentType.PartTime);

        EmploymentTypeResult updated = await Types(provider).UpdateAsync(
            partTime.Id,
            Configured(name: partTime.Name, hoursPerWeek: 24m, leaveDays: 18));

        Assert.True(updated.Succeeded, updated.Outcome.ToString());
        Assert.Equal(24m, updated.Type!.HoursPerWeek);
        Assert.Equal(18, updated.Type.DefaultLeaveDays);

        // Still the built-in kind, so employees keep pointing at it.
        Assert.Equal(EmploymentType.PartTime, updated.Type.Kind);
    }
}
