using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Onboarding;
using Motee.Application.Organisation;
using Motee.Domain.Approvals;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Onboarding;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class OnboardingServiceTests(PostgresFixture fixture)
{
    private async Task<(Guid TenantId, Guid DepartmentId, ServiceProvider Provider)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        Guid tenantId = await SeedTenantAsync();

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        DepartmentResult department = await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" });

        return (tenantId, department.Department!.Id, provider);
    }

    private async Task<Guid> SeedTenantAsync()
    {
        Guid tenantId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext();

        seed.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Acme Corporation",
            Slug = $"acme-{tenantId:N}",
            CountryCode = CountryCode.Nigeria,
        });

        await seed.SaveChangesAsync();

        return tenantId;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static EmployeeRequest Employee(
        Guid departmentId,
        string email = "ada@acme.com",
        DateOnly? startDate = null) => new()
    {
        FirstName = "Ada",
        LastName = "Okafor",
        Email = email,
        JobTitle = "Engineer",
        DepartmentId = departmentId,
        EmploymentType = EmploymentType.FullTime,
        StartDate = startDate,
    };

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        Guid departmentId,
        OnboardingMethod via = OnboardingMethod.Manual,
        string email = "ada@acme.com",
        DateOnly? startDate = null)
    {
        EmployeeResult result = await Resolve<IEmployeeService>(provider)
            .CreateAsync(Employee(departmentId, email, startDate), via);

        Assert.True(result.Succeeded, result.Outcome.ToString());

        return result.Employee!.Id;
    }

    // Whichever route somebody came in by, they appear in the pipeline. An employee saved
    // without a record would be invisible to it, and nothing would go back and notice.
    [SkippableFact]
    public async Task HiringSomebodyPutsThemInThePipeline()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId);

        PagedResult<OnboardingDto> pipeline =
            await Resolve<IOnboardingService>(owned).ListAsync(new OnboardingQuery());

        OnboardingDto record = Assert.Single(pipeline.Items);

        Assert.Equal(employeeId, record.EmployeeId);
        Assert.Equal(OnboardingStage.PreBoarding, record.Stage);
        Assert.Equal(OnboardingSubmission.NotStarted, record.Submission);
        Assert.Equal("Ada Okafor", record.EmployeeName);
    }

    // An employee has one onboarding. A second call has to hand back the first rather
    // than start a rival that would split the record in two.
    [SkippableFact]
    public async Task EnsuringTwiceDoesNotStartASecondOnboarding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId);

        IOnboardingService onboarding = Resolve<IOnboardingService>(owned);

        OnboardingRecord again = await onboarding.EnsureAsync(employeeId);

        await using MoteeDbContext context = fixture.CreateContext();

        Assert.Single(await context.OnboardingRecords
            .IgnoreQueryFilters()
            .Where(record => record.EmployeeId == employeeId)
            .ToListAsync());

        Assert.Equal(employeeId, again.EmployeeId);
    }

    // The route where the joiner filled the form in themselves is the one somebody else
    // has to check.
    [SkippableFact]
    public async Task SubmittingAnInvitedJoinersPackStartsAReview()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await SeedDefaultChainAsync(tenantId, departmentId);

        Guid employeeId = await HireAsync(owned, departmentId, OnboardingMethod.Invite);

        OnboardingResult submitted = await Resolve<IOnboardingService>(owned).SubmitAsync(employeeId);

        Assert.True(submitted.Succeeded, submitted.Outcome.ToString());
        Assert.Equal(OnboardingSubmission.Submitted, submitted.Record!.Submission);
        Assert.NotNull(submitted.Record.Review);
        Assert.Equal(ApprovalDocumentTypes.Onboarding, submitted.Record.Review.DocumentType);
    }

    // HR typed this record themselves. There is nothing for a manager to check that HR
    // did not already have in front of them, and a rubber-stamp chain here is how real
    // approvals end up buried among noise.
    [SkippableFact]
    public async Task SubmittingARecordHrTypedThemselvesStartsNoReview()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await SeedDefaultChainAsync(tenantId, departmentId);

        Guid employeeId = await HireAsync(owned, departmentId, OnboardingMethod.Manual);

        OnboardingResult submitted = await Resolve<IOnboardingService>(owned).SubmitAsync(employeeId);

        Assert.True(submitted.Succeeded, submitted.Outcome.ToString());
        Assert.Null(submitted.Record!.Review);
    }

    [SkippableFact]
    public async Task APackCannotBeHandedInTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId);

        IOnboardingService onboarding = Resolve<IOnboardingService>(owned);

        Assert.True((await onboarding.SubmitAsync(employeeId)).Succeeded);

        Assert.Equal(
            OnboardingOutcome.NotAllowed,
            (await onboarding.SubmitAsync(employeeId)).Outcome);
    }

    // The whole reason the guarded transition exists. Closing a record whose pack nobody
    // looked at makes "onboarding complete" mean nothing.
    [SkippableFact]
    public async Task CompletingIsRefusedWhileTheReviewIsOutstanding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await SeedDefaultChainAsync(tenantId, departmentId);

        Guid employeeId = await HireAsync(owned, departmentId, OnboardingMethod.Invite);

        IOnboardingService onboarding = Resolve<IOnboardingService>(owned);

        OnboardingResult submitted = await onboarding.SubmitAsync(employeeId);
        Assert.NotNull(submitted.Record!.Review);

        Assert.Equal(
            OnboardingOutcome.ReviewIncomplete,
            (await onboarding.CompleteAsync(submitted.Record.Id)).Outcome);
    }

    // Nothing was submitted at all, which is a different problem and sends HR somewhere
    // different — chase the joiner, not the approver.
    [SkippableFact]
    public async Task CompletingIsRefusedWhenNothingWasSubmitted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await HireAsync(owned, departmentId);

        IOnboardingService onboarding = Resolve<IOnboardingService>(owned);

        OnboardingDto record = Assert.Single(
            (await onboarding.ListAsync(new OnboardingQuery())).Items);

        Assert.Equal(
            OnboardingOutcome.NotAllowed,
            (await onboarding.CompleteAsync(record.Id)).Outcome);
    }

    // A record with no chain is approved by definition: HR entered everything themselves.
    // Neither is the joiner's doing, and neither is a reason to leave them onboarding
    // forever.
    [SkippableFact]
    public async Task ARecordWithNoReviewCanBeCompleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId);

        IOnboardingService onboarding = Resolve<IOnboardingService>(owned);

        OnboardingResult submitted = await onboarding.SubmitAsync(employeeId);
        Assert.Null(submitted.Record!.Review);

        OnboardingResult completed = await onboarding.CompleteAsync(submitted.Record.Id);

        Assert.True(completed.Succeeded, completed.Outcome.ToString());
        Assert.Equal(OnboardingStage.Completed, completed.Record!.Stage);
        Assert.NotNull(completed.Record.CompletedAt);
    }

    [SkippableFact]
    public async Task StagesMoveInBothDirectionsButNotOutOfCompleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId);

        IOnboardingService onboarding = Resolve<IOnboardingService>(owned);

        OnboardingResult submitted = await onboarding.SubmitAsync(employeeId);
        Guid id = submitted.Record!.Id;

        Assert.Equal(
            OnboardingStage.ThirtyDay,
            (await onboarding.MoveAsync(id, OnboardingStage.ThirtyDay)).Record!.Stage);

        Assert.Equal(
            OnboardingStage.DayOne,
            (await onboarding.MoveAsync(id, OnboardingStage.DayOne)).Record!.Stage);

        Assert.True((await onboarding.CompleteAsync(id)).Succeeded);

        Assert.Equal(
            OnboardingOutcome.NotAllowed,
            (await onboarding.MoveAsync(id, OnboardingStage.ThirtyDay)).Outcome);
    }

    // The Overdue filter runs in SQL and the flag is computed in C#, because the
    // projection cannot reach across to the employee for the start date. Two hand-written
    // copies of one rule is exactly the shape that drifts, so this pins them together:
    // whatever the filter returns is whatever the flag marks.
    [SkippableFact]
    public async Task TheOverdueFilterAndTheOverdueFlagAgree()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Started last month, nothing submitted. The one HR opened the screen to find.
        await HireAsync(owned, departmentId, email: "late@acme.com",
            startDate: today.AddMonths(-1));

        // Starts next month. Not late — they have not started.
        await HireAsync(owned, departmentId, email: "future@acme.com",
            startDate: today.AddMonths(1));

        // No start date recorded at all.
        await HireAsync(owned, departmentId, email: "undated@acme.com");

        // Started last month and handed their pack in.
        Guid punctual = await HireAsync(owned, departmentId, email: "done@acme.com",
            startDate: today.AddMonths(-1));

        IOnboardingService onboarding = Resolve<IOnboardingService>(owned);
        await onboarding.SubmitAsync(punctual);

        IReadOnlyList<OnboardingDto> everyone =
            (await onboarding.ListAsync(new OnboardingQuery())).Items;

        Assert.Equal(4, everyone.Count);

        HashSet<Guid> flagged = [.. everyone.Where(r => r.IsOverdue).Select(r => r.Id)];

        HashSet<Guid> filtered =
        [
            .. (await onboarding.ListAsync(new OnboardingQuery { Overdue = true })).Items
                .Select(r => r.Id),
        ];

        Assert.Equal(flagged, filtered);

        // And it actually found the right person, rather than agreeing on nothing.
        OnboardingDto late = Assert.Single(everyone, r => r.IsOverdue);
        Assert.Equal("late@acme.com", late.Email);

        // The complement has to agree too, or the two filters together lose a row.
        HashSet<Guid> notFiltered =
        [
            .. (await onboarding.ListAsync(new OnboardingQuery { Overdue = false })).Items
                .Select(r => r.Id),
        ];

        Assert.Equal([.. everyone.Where(r => !r.IsOverdue).Select(r => r.Id)], notFiltered);
    }

    // Onboarding records carry a person's start date, department and progress. Another
    // company's pipeline must not be reachable through this service at all.
    [SkippableFact]
    public async Task OneCompanysPipelineIsNotVisibleToAnother()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await HireAsync(owned, departmentId);

        OnboardingDto theirs = Assert.Single(
            (await Resolve<IOnboardingService>(owned).ListAsync(new OnboardingQuery())).Items);

        Guid otherTenantId = await SeedTenantAsync();

        await using ServiceProvider intruder = fixture.BuildProvider();
        intruder.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = otherTenantId;

        IOnboardingService onboarding = Resolve<IOnboardingService>(intruder);

        Assert.Empty((await onboarding.ListAsync(new OnboardingQuery())).Items);
        Assert.Null(await onboarding.GetAsync(theirs.Id));
        Assert.Equal(OnboardingOutcome.NotFound, (await onboarding.CompleteAsync(theirs.Id)).Outcome);
        Assert.Equal(
            OnboardingOutcome.NotFound,
            (await onboarding.MoveAsync(theirs.Id, OnboardingStage.DayOne)).Outcome);
    }

    private async Task SeedDefaultChainAsync(Guid tenantId, Guid departmentId)
    {
        await using MoteeDbContext seed = fixture.CreateContext();

        Guid templateId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        seed.ApprovalTemplates.Add(new ApprovalTemplate
        {
            Id = templateId,
            TenantId = tenantId,
            DocumentType = ApprovalDocumentTypes.Onboarding,
            Name = "Standard onboarding",
            IsDefault = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Department head rather than line manager: the joiners in these tests have no
        // manager set, and a chain that resolves to nobody is a different test.
        seed.ApprovalTemplateSteps.Add(new ApprovalTemplateStep
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TemplateId = templateId,
            Sequence = 0,
            Label = "Department head approval",
            Approver = ApproverResolver.DepartmentHead,
            Required = false,
        });

        // A head who actually exists, and an account for them. A chain that resolves to
        // nobody skips its optional step and settles immediately, which would make these
        // tests pass without any review ever happening.
        Guid headId = Guid.NewGuid();

        seed.Employees.Add(new Employee
        {
            Id = headId,
            TenantId = tenantId,
            FirstName = "Cara",
            LastName = "Nwosu",
            Email = "cara@acme.com",
            DepartmentId = departmentId,
            Status = EmployeeStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });

        seed.Users.Add(new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EmployeeId = headId,
            FirstName = "Cara",
            LastName = "Nwosu",
            Email = "cara@acme.com",
            NormalizedEmail = "CARA@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = now,
        });

        Department? department = await seed.Departments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == departmentId);

        department!.HeadEmployeeId = headId;

        await seed.SaveChangesAsync();
    }
}
