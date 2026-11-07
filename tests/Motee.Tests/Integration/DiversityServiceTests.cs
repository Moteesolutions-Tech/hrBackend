using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Diversity;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Diversity;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Self-declared diversity data, end to end.
//
// These tests are mostly about what the service refuses. The data is
// special-category under UK GDPR and sensitive under Nigeria's NDPA, and the
// failure mode is not an exception — it is a chart with a group of two on it
// that names those two people to anybody who knows the team, or an API that
// quietly accepts an ethnicity for a country that deliberately does not ask.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class DiversityServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 11, 2).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _otherDepartmentId;

    private async Task<ServiceProvider> ArrangeAsync(string country = "GB")
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

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        IDepartmentService departments = Resolve<IDepartmentService>(provider);

        _departmentId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _otherDepartmentId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IDiversityService Diversity(ServiceProvider provider) =>
        Resolve<IDiversityService>(provider);

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid? departmentId = null,
        EmployeeStatus status = EmployeeStatus.Active) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId ?? _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = status,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task ActAsAsync(ServiceProvider provider, Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
            {
                Id = userId,
                TenantId = _tenantId,
                EmployeeId = employeeId,
                FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
                LastName = "Okafor",
                Email = $"{handle}-login@acme.com",
                NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }

    // Written straight in: there is no API that declares on somebody else's
    // behalf, which is the point, so a population has to be seeded directly.
    private async Task DeclaredAsync(
        Guid employeeId,
        string? ethnicity = null,
        string? religion = null,
        string? disability = null)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.DiversityDeclarations.Add(new DiversityDeclaration
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            Ethnicity = ethnicity,
            Religion = religion,
            Disability = disability,
            Jurisdiction = Motee.Domain.Common.CountryCode.UnitedKingdom,
            DeclaredAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    // ── the form ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheFormAsksThisCountrysQuestions()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DiversityFormDto form = await Diversity(provider).MineAsync();

        Assert.Equal(5, form.Categories.Count);
        Assert.Contains(form.Categories, category => category.Field == DiversityField.Ethnicity);
        Assert.Null(form.DeclaredAt);

        // Every question explains itself, to the person being asked.
        Assert.All(
            form.Categories,
            category => Assert.False(string.IsNullOrWhiteSpace(category.Purpose)));
    }

    [SkippableFact]
    public async Task ANigerianCompanyIsNotAskedAboutEthnicity()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("NG");

        DiversityFormDto form = await Diversity(provider).MineAsync();

        Assert.Equal(3, form.Categories.Count);
        Assert.DoesNotContain(
            form.Categories, category => category.Field == DiversityField.Ethnicity);
    }

    // ── declaring ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SomebodyDeclaresTheirOwnAnswers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid cara = await HireAsync(provider, "Cara");
        await ActAsAsync(provider, cara, "cara");

        DiversityResult result = await Diversity(provider).DeclareAsync(new DiversityRequest
        {
            Ethnicity = "White",
            Religion = "No religion",
        });

        Assert.True(result.Succeeded, result.Outcome.ToString());
        Assert.Equal(
            "White",
            Assert.Single(result.Form!.Categories, c => c.Field == DiversityField.Ethnicity)
                .Answer);
        Assert.NotNull(result.Form.DeclaredAt);
    }

    [SkippableFact]
    public async Task DeclaringAgainUpdatesTheSameRow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid cara = await HireAsync(provider, "Cara");
        await ActAsAsync(provider, cara, "cara");

        await Diversity(provider).DeclareAsync(new DiversityRequest { Religion = "Christian" });
        await Diversity(provider).DeclareAsync(new DiversityRequest { Religion = "No religion" });

        await using MoteeDbContext check = fixture.CreateContext(_tenantId);

        Assert.Equal(
            "No religion",
            (await check.DiversityDeclarations.SingleAsync()).Religion);
    }

    // Leaving a question out of the request means "leave what I said" rather
    // than "erase it". Clearing is done by choosing to decline, which is an
    // answer.
    [SkippableFact]
    public async Task AnAnswerLeftOutOfTheRequestIsNotErased()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid cara = await HireAsync(provider, "Cara");
        await ActAsAsync(provider, cara, "cara");

        await Diversity(provider).DeclareAsync(
            new DiversityRequest { Ethnicity = "White", Religion = "Christian" });

        DiversityResult second = await Diversity(provider).DeclareAsync(
            new DiversityRequest { Religion = "No religion" });

        Assert.Equal(
            "White",
            Assert.Single(second.Form!.Categories, c => c.Field == DiversityField.Ethnicity)
                .Answer);
    }

    [SkippableFact]
    public async Task DecliningIsRecordedAsAnAnswer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid cara = await HireAsync(provider, "Cara");
        await ActAsAsync(provider, cara, "cara");

        DiversityResult result = await Diversity(provider).DeclareAsync(
            new DiversityRequest { Ethnicity = Diversities.PreferNotToSay });

        Assert.Equal(
            Diversities.PreferNotToSay,
            Assert.Single(result.Form!.Categories, c => c.Field == DiversityField.Ethnicity)
                .Answer);
    }

    [SkippableFact]
    public async Task AnAnswerNobodyWasOfferedIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid cara = await HireAsync(provider, "Cara");
        await ActAsAsync(provider, cara, "cara");

        DiversityResult result = await Diversity(provider).DeclareAsync(
            new DiversityRequest { Religion = "Jedi" });

        Assert.Equal(DiversityOutcome.AnswerNotOffered, result.Outcome);
        Assert.Equal(DiversityField.Religion, result.Field);
    }

    // The guarantee a frontend structurally cannot make. The Nigerian form
    // does not show an ethnicity question; the API refuses the answer even
    // when a client sends one anyway.
    [SkippableFact]
    public async Task AnEthnicityForANigerianCompanyIsRefusedByTheServer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("NG");

        Guid cara = await HireAsync(provider, "Cara");
        await ActAsAsync(provider, cara, "cara");

        DiversityResult result = await Diversity(provider).DeclareAsync(
            new DiversityRequest { Ethnicity = "White" });

        Assert.Equal(DiversityOutcome.AnswerNotOffered, result.Outcome);
        Assert.Equal(DiversityField.Ethnicity, result.Field);

        await using MoteeDbContext check = fixture.CreateContext(_tenantId);

        Assert.False(await check.DiversityDeclarations.AnyAsync());
    }

    // The account owner runs the company rather than working there, so there
    // is nothing for them to declare against.
    [SkippableFact]
    public async Task AnAccountWithNoEmployeeRecordHasNothingToDeclare()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            DiversityOutcome.NoEmployeeRecord,
            (await Diversity(provider).DeclareAsync(
                new DiversityRequest { Religion = "Christian" })).Outcome);
    }

    // Nobody can read anybody else's. MineAsync resolves the caller, so
    // acting as a different person returns that person's own answers and
    // never the first person's.
    [SkippableFact]
    public async Task OnePersonsAnswersAreNotVisibleToAnother()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid cara = await HireAsync(provider, "Cara");
        Guid bola = await HireAsync(provider, "Bola");

        await ActAsAsync(provider, cara, "cara");
        await Diversity(provider).DeclareAsync(new DiversityRequest { Religion = "Christian" });

        await ActAsAsync(provider, bola, "bola");

        DiversityFormDto bolas = await Diversity(provider).MineAsync();

        Assert.Null(bolas.DeclaredAt);
        Assert.All(bolas.Categories, category => Assert.Null(category.Answer));
    }

    // ── reporting ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AGroupBigEnoughToBeSafeIsReported()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        for (int index = 0; index < 6; index++)
        {
            await DeclaredAsync(
                await HireAsync(provider, $"Person{index}"), ethnicity: "White");
        }

        DiversityReportDto report = await Diversity(provider).ReportAsync(
            new DiversityReportQuery());

        DiversityBreakdownDto ethnicity = Assert.Single(
            report.Breakdowns, row => row.Field == DiversityField.Ethnicity);

        Assert.Equal(6, Assert.Single(ethnicity.Rows).Count);
        Assert.Equal(6, report.Declared);
        Assert.Equal(6, report.Eligible);
        Assert.Equal(100, report.DeclarationRatePercent);
    }

    // The rule the whole module exists for. Four people in a group is four
    // people a reader who knows the team can name.
    [SkippableFact]
    public async Task AGroupTooSmallToBeSafeIsFoldedAway()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        for (int index = 0; index < 6; index++)
        {
            await DeclaredAsync(
                await HireAsync(provider, $"Person{index}"), ethnicity: "White");
        }

        for (int index = 0; index < 3; index++)
        {
            await DeclaredAsync(
                await HireAsync(provider, $"Other{index}"),
                ethnicity: "Mixed or multiple ethnic groups");
        }

        DiversityBreakdownDto ethnicity = Assert.Single(
            (await Diversity(provider).ReportAsync(new DiversityReportQuery())).Breakdowns,
            row => row.Field == DiversityField.Ethnicity);

        Assert.Equal("White", Assert.Single(ethnicity.Rows).Label);
        Assert.Equal(3, ethnicity.SuppressedCount);
        Assert.Equal(1, ethnicity.SuppressedGroups);
        Assert.False(ethnicity.FullySuppressed);
    }

    // An empty chart would read as "we collect no data". Saying it is fully
    // suppressed says the other thing.
    [SkippableFact]
    public async Task ABreakdownWithNothingSafeToSayIsWithheldAndSaysSo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await DeclaredAsync(await HireAsync(provider, "Cara"), ethnicity: "White");
        await DeclaredAsync(await HireAsync(provider, "Bola"), ethnicity: "White");

        DiversityBreakdownDto ethnicity = Assert.Single(
            (await Diversity(provider).ReportAsync(new DiversityReportQuery())).Breakdowns,
            row => row.Field == DiversityField.Ethnicity);

        Assert.Empty(ethnicity.Rows);
        Assert.True(ethnicity.FullySuppressed);
        Assert.Equal(2, ethnicity.SuppressedCount);
    }

    // The report is counts. Nothing in the shape of the response can be
    // paired with a person, which is why there is no id anywhere in it.
    [SkippableFact]
    public async Task TheReportCarriesNoIdentityAtAll()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        for (int index = 0; index < 6; index++)
        {
            await DeclaredAsync(
                await HireAsync(provider, $"Person{index}"), religion: "Christian");
        }

        DiversityReportDto report = await Diversity(provider).ReportAsync(
            new DiversityReportQuery());

        string serialised = System.Text.Json.JsonSerializer.Serialize(report);

        Assert.DoesNotContain("Person0", serialised, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Okafor", serialised, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("employeeId", serialised, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task TheDeclarationRateReportsTheDatasOwnHealth()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await DeclaredAsync(await HireAsync(provider, "Cara"), religion: "Christian");
        await HireAsync(provider, "Bola");
        await HireAsync(provider, "Dele");
        await HireAsync(provider, "Emeka");

        DiversityReportDto report = await Diversity(provider).ReportAsync(
            new DiversityReportQuery());

        Assert.Equal(4, report.Eligible);
        Assert.Equal(1, report.Declared);
        Assert.Equal(25, report.DeclarationRatePercent);
    }

    [SkippableFact]
    public async Task SomebodyWhoHasLeftIsNotInThePopulation()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara");
        await HireAsync(provider, "Bola", status: EmployeeStatus.Inactive);

        Assert.Equal(
            1,
            (await Diversity(provider).ReportAsync(new DiversityReportQuery())).Eligible);
    }

    [SkippableFact]
    public async Task TheReportNarrowsToADepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara");
        await HireAsync(provider, "Bola");
        await HireAsync(provider, "Dele", _otherDepartmentId);

        Assert.Equal(
            1,
            (await Diversity(provider).ReportAsync(
                new DiversityReportQuery { DepartmentId = _otherDepartmentId })).Eligible);
    }

    // What the workforce lens passes when its filters are on: ids in, counts
    // out, and nothing comes back that could be paired with one of them.
    [SkippableFact]
    public async Task TheReportNarrowsToANamedSetOfPeople()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid cara = await HireAsync(provider, "Cara");
        await HireAsync(provider, "Bola");

        DiversityReportDto report = await Diversity(provider).ReportAsync(
            new DiversityReportQuery { EmployeeIds = [cara] });

        Assert.Equal(1, report.Eligible);
    }

    [SkippableFact]
    public async Task ANigerianReportHasNoEthnicityBreakdownToSuppress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("NG");

        await HireAsync(provider, "Cara");

        DiversityReportDto report = await Diversity(provider).ReportAsync(
            new DiversityReportQuery());

        Assert.Equal(3, report.Breakdowns.Count);
        Assert.DoesNotContain(
            report.Breakdowns, row => row.Field == DiversityField.Ethnicity);
    }
}
