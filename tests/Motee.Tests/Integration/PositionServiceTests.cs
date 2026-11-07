using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Recruitment;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The establishment, out of the database.
//
// The status is what these prove. It is derived from who holds a post and
// what is being recruited into it, so the interesting cases are the ones
// where a stored label would have drifted: somebody placed into a post, taken
// out again, a vacancy raised against it, the post retired.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class PositionServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 11, 3).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        IDepartmentService departments = Resolve<IDepartmentService>(provider);

        _engineeringId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _salesId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IPositionService Posts(ServiceProvider provider) =>
        Resolve<IPositionService>(provider);

    private PositionRequest Post(
        string title = "Backend engineer",
        int headcount = 1,
        Guid? departmentId = null) => new()
        {
            Title = title,
            DepartmentId = departmentId ?? _engineeringId,
            Grade = "Senior",
            Headcount = headcount,
        };

    private static async Task<PositionDto> CreatedAsync(
        ServiceProvider provider,
        PositionRequest request)
    {
        PositionResult result = await Posts(provider).CreateAsync(request);

        Assert.True(result.Succeeded, result.Outcome.ToString());

        return result.Position!;
    }

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _engineeringId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task RecruitingAsync(Guid positionId, int openings)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.JobRequisitions.Add(new JobRequisition
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            PositionTitle = "Backend engineer",
            DepartmentId = _engineeringId,
            PositionId = positionId,
            Status = RequisitionStatus.Open,
            Openings = openings,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    // ── creating ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewPostStartsVacant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post(headcount: 3));

        Assert.Equal(PositionStatus.Vacant, post.Status);
        Assert.Equal(3, post.Headcount);
        Assert.Equal(0, post.Assigned);
        Assert.Equal(3, post.OpeningsLeft);
        Assert.Equal("Engineering", post.DepartmentName);
        Assert.Empty(post.Holders);
    }

    // The same title in two departments is ordinary — a sales manager and an
    // engineering manager are different posts.
    [SkippableFact]
    public async Task TheSameTitleInTwoDepartmentsIsFine()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Post(title: "Manager"));
        await CreatedAsync(provider, Post(title: "Manager", departmentId: _salesId));
    }

    // Two in one department is two answers to "which is Sarah in".
    [SkippableFact]
    public async Task TheSameTitleTwiceInOneDepartmentIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Post(title: "Manager"));

        PositionResult again = await Posts(provider).CreateAsync(Post(title: "MANAGER"));

        Assert.Equal(PositionRefusal.TitleTakenInDepartment, again.Refusal);
    }

    [SkippableFact]
    public async Task APostForNobodyIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            PositionRefusal.HeadcountNotPositive,
            (await Posts(provider).CreateAsync(Post(headcount: 0))).Refusal);
    }

    [SkippableFact]
    public async Task APostInADepartmentThatDoesNotExistIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            PositionOutcome.DepartmentNotFound,
            (await Posts(provider).CreateAsync(
                Post(departmentId: Guid.NewGuid()))).Outcome);
    }

    // ── who holds it ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task PlacingSomebodyInAPostFillsIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());
        Guid cara = await HireAsync(provider, "Cara");

        PositionResult placed = await Posts(provider).AssignAsync(post.Id, cara);

        Assert.True(placed.Succeeded, placed.Outcome.ToString());
        Assert.Equal(PositionStatus.Filled, placed.Position!.Status);
        Assert.Equal(1, placed.Position.Assigned);
        Assert.Equal(0, placed.Position.OpeningsLeft);
        Assert.Equal("Cara Okafor", Assert.Single(placed.Position.Holders).Name);
    }

    // Three seats, one person, nothing being recruited: vacant, not filled.
    // The distinction is the whole reason a post carries a complement.
    [SkippableFact]
    public async Task APartlyStaffedPostIsNotFilled()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post(headcount: 3));
        await Posts(provider).AssignAsync(post.Id, await HireAsync(provider, "Cara"));

        PositionDto after = (await Posts(provider).GetAsync(post.Id))!;

        Assert.Equal(PositionStatus.Vacant, after.Status);
        Assert.Equal(2, after.OpeningsLeft);
    }

    [SkippableFact]
    public async Task TakingSomebodyOutEmptiesThePostAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());
        Guid cara = await HireAsync(provider, "Cara");

        await Posts(provider).AssignAsync(post.Id, cara);

        PositionResult removed = await Posts(provider).UnassignAsync(cara);

        Assert.True(removed.Succeeded, removed.Outcome.ToString());
        Assert.Equal(PositionStatus.Vacant, removed.Position!.Status);
        Assert.Empty(removed.Position.Holders);
    }

    // Still the outcome the caller asked for, with nothing to describe.
    [SkippableFact]
    public async Task TakingSomebodyOutOfNoPostStillSucceeds()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionResult removed = await Posts(provider).UnassignAsync(
            await HireAsync(provider, "Cara"));

        Assert.True(removed.Succeeded, removed.Outcome.ToString());
        Assert.Null(removed.Position);
    }

    // Handover overlaps are real. A system that blocks them makes somebody
    // record a lie to get the work done.
    [SkippableFact]
    public async Task APostCanCarryMorePeopleThanItsComplement()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());

        await Posts(provider).AssignAsync(post.Id, await HireAsync(provider, "Cara"));
        PositionResult second = await Posts(provider).AssignAsync(
            post.Id, await HireAsync(provider, "Bola"));

        Assert.True(second.Succeeded, second.Outcome.ToString());
        Assert.Equal(2, second.Position!.Assigned);
        Assert.Equal(PositionStatus.Filled, second.Position.Status);
        Assert.Equal(0, second.Position.OpeningsLeft);
    }

    [SkippableFact]
    public async Task PlacingSomebodyWhoDoesNotWorkHereIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());

        Assert.Equal(
            PositionOutcome.EmployeeNotFound,
            (await Posts(provider).AssignAsync(post.Id, Guid.NewGuid())).Outcome);
    }

    // ── what is being recruited into it ─────────────────────────────────────

    [SkippableFact]
    public async Task AVacancyAgainstAPostMakesItRecruiting()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post(headcount: 2));
        await RecruitingAsync(post.Id, openings: 2);

        PositionDto after = (await Posts(provider).GetAsync(post.Id))!;

        Assert.Equal(PositionStatus.Recruiting, after.Status);
        Assert.Equal(2, after.Recruiting);
    }

    // Filled outranks recruiting: a post at complement with a stray open
    // vacancy is filled, and the vacancy is the thing that is wrong.
    [SkippableFact]
    public async Task AFullPostIsFilledEvenWithAVacancyStillOpen()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());
        await Posts(provider).AssignAsync(post.Id, await HireAsync(provider, "Cara"));
        await RecruitingAsync(post.Id, openings: 1);

        Assert.Equal(
            PositionStatus.Filled, (await Posts(provider).GetAsync(post.Id))!.Status);
    }

    // ── retiring ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnEmptyPostIsRetiredAndRestored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());

        PositionResult retired = await Posts(provider).RetireAsync(post.Id);

        Assert.Equal(PositionStatus.Inactive, retired.Position!.Status);
        Assert.False(retired.Position.IsActive);

        Assert.Equal(
            PositionStatus.Vacant, (await Posts(provider).RestoreAsync(post.Id)).Position!.Status);
    }

    // Retiring a post somebody is in would leave their record pointing at
    // something the company says no longer exists.
    [SkippableFact]
    public async Task APostSomebodyIsInCannotBeRetired()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());
        await Posts(provider).AssignAsync(post.Id, await HireAsync(provider, "Cara"));

        PositionResult retired = await Posts(provider).RetireAsync(post.Id);

        Assert.Equal(PositionOutcome.Refused, retired.Outcome);
        Assert.Equal(PositionRefusal.InUse, retired.Refusal);
    }

    [SkippableFact]
    public async Task APostSomebodyIsInCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());
        await Posts(provider).AssignAsync(post.Id, await HireAsync(provider, "Cara"));

        Assert.Equal(PositionOutcome.Refused, await Posts(provider).DeleteAsync(post.Id));
    }

    [SkippableFact]
    public async Task AnEmptyPostIsDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());

        Assert.Equal(PositionOutcome.Succeeded, await Posts(provider).DeleteAsync(post.Id));
        Assert.Null(await Posts(provider).GetAsync(post.Id));
    }

    // ── the figures ─────────────────────────────────────────────────────────

    // The measure of whether this module is being kept up at all: a company
    // that has never filled it in sees that plainly rather than seeing zeros.
    [SkippableFact]
    public async Task TheSummaryReportsPeopleInNoPostAtAll()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post(headcount: 2));

        await Posts(provider).AssignAsync(post.Id, await HireAsync(provider, "Cara"));
        await HireAsync(provider, "Bola");
        await HireAsync(provider, "Dele");

        PositionSummaryDto summary = await Posts(provider).SummaryAsync();

        Assert.Equal(1, summary.Positions);
        Assert.Equal(2, summary.Establishment);
        Assert.Equal(1, summary.Filled);
        Assert.Equal(1, summary.OpeningsLeft);
        Assert.Equal(2, summary.UnassignedEmployees);
    }

    [SkippableFact]
    public async Task TheSummaryCountsEachStatus()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto vacant = await CreatedAsync(provider, Post(title: "Vacant post"));
        PositionDto hiring = await CreatedAsync(provider, Post(title: "Hiring post"));
        PositionDto gone = await CreatedAsync(provider, Post(title: "Gone post"));

        await RecruitingAsync(hiring.Id, openings: 1);
        await Posts(provider).RetireAsync(gone.Id);

        PositionSummaryDto summary = await Posts(provider).SummaryAsync();

        Assert.Equal(1, summary.Vacant);
        Assert.Equal(1, summary.Recruiting);
        Assert.Equal(1, summary.Retired);

        // Retired posts are out of the establishment: a post the company has
        // closed is not headcount it is carrying.
        Assert.Equal(2, summary.Positions);
        Assert.Equal(2, summary.Establishment);

        Assert.NotEqual(vacant.Id, hiring.Id);
    }

    // ── listing ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheListNarrowsByStatusEvenThoughStatusIsDerived()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto filled = await CreatedAsync(provider, Post(title: "Filled post"));
        await CreatedAsync(provider, Post(title: "Empty post"));

        await Posts(provider).AssignAsync(filled.Id, await HireAsync(provider, "Cara"));

        Assert.Equal(
            "Filled post",
            Assert.Single(await Posts(provider).ListAsync(
                new PositionQuery { Status = PositionStatus.Filled })).Title);

        Assert.Equal(
            "Empty post",
            Assert.Single(await Posts(provider).ListAsync(
                new PositionQuery { Status = PositionStatus.Vacant })).Title);
    }

    [SkippableFact]
    public async Task TheListNarrowsToADepartmentAndSearchesByTitle()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Post(title: "Backend engineer"));
        await CreatedAsync(provider, Post(title: "Account executive", departmentId: _salesId));

        Assert.Equal(
            "Account executive",
            Assert.Single(await Posts(provider).ListAsync(
                new PositionQuery { DepartmentId = _salesId })).Title);

        Assert.Equal(
            "Backend engineer",
            Assert.Single(await Posts(provider).ListAsync(
                new PositionQuery { Search = "backend" })).Title);
    }

    [SkippableFact]
    public async Task RetiredPostsCanBeHiddenFromTheList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PositionDto post = await CreatedAsync(provider, Post());
        await Posts(provider).RetireAsync(post.Id);

        Assert.Single(await Posts(provider).ListAsync(new PositionQuery()));

        Assert.Empty(await Posts(provider).ListAsync(
            new PositionQuery { IncludeInactive = false }));
    }
}
