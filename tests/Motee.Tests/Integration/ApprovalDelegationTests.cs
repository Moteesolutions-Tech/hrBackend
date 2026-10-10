using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Approvals;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// "I am away this week — send my approvals to her."
//
// The behaviour that matters is that the redirect explains itself afterwards: the delegate
// did not become anybody's manager, they were temporarily authorised, and a year later
// that difference is the whole question.
[Collection(PostgresCollection.Name)]
public class ApprovalDelegationTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _managerId;
    private Guid _managerUserId;
    private Guid _coverId;
    private Guid _coverUserId;
    private Guid _subjectId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();
        _managerId = Guid.NewGuid();
        _managerUserId = Guid.NewGuid();
        _coverId = Guid.NewGuid();
        _coverUserId = Guid.NewGuid();
        _subjectId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = _tenantId,
                Name = "Acme",
                Slug = $"acme-{_tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Add(seed, _managerId, _managerUserId, "Ada");
            Add(seed, _coverId, _coverUserId, "Cara");

            seed.Employees.Add(new Employee
            {
                Id = _subjectId,
                TenantId = _tenantId,
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
                ManagerId = _managerId,
                Status = EmployeeStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        return provider;
    }

    private void Add(MoteeDbContext seed, Guid employeeId, Guid userId, string first)
    {
        seed.Employees.Add(new Employee
        {
            Id = employeeId,
            TenantId = _tenantId,
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Status = EmployeeStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        seed.Users.Add(new ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            NormalizedEmail = $"{first.ToUpperInvariant()}@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static T As<T>(ServiceProvider provider, Guid userId) where T : notnull
    {
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId = userId.ToString();

        return provider.CreateScope().ServiceProvider.GetRequiredService<T>();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private Task<ApprovalDelegationResult> DelegateAsync(
        ServiceProvider provider,
        int fromDay = 0,
        int toDay = 7,
        Guid? to = null,
        string? reason = "Annual leave") =>
        As<IApprovalDelegationService>(provider, _managerUserId).CreateAsync(
            new ApprovalDelegationRequest
            {
                DelegateEmployeeId = to ?? _coverId,
                StartDate = Today.AddDays(fromDay),
                EndDate = Today.AddDays(toDay),
                Reason = reason,
            });

    private async Task<Guid> TemplateAsync(ServiceProvider provider)
    {
        ApprovalTemplateResult created = await provider.CreateScope().ServiceProvider
            .GetRequiredService<IApprovalTemplateService>()
            .CreateAsync(new ApprovalTemplateRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                Name = "Leave approval",
                IsDefault = true,
                Steps =
                [
                    new ApprovalTemplateStepRequest
                    {
                        Label = "Line manager approval",
                        Approver = ApproverResolver.LineManager,
                        Required = true,
                    },
                ],
            });

        Assert.True(created.Succeeded, created.Outcome.ToString());

        return created.Template!.Id;
    }

    private Task<ApprovalResult> StartAsync(ServiceProvider provider) =>
        As<IApprovalService>(provider, _coverUserId).StartAsync(new StartApprovalRequest
        {
            DocumentType = ApprovalDocumentTypes.LeaveRequest,
            SubjectType = "LeaveRequest",
            SubjectId = Guid.NewGuid(),
            SubjectEmployeeId = _subjectId,
        });

    // The point of the feature: work goes to the person covering, not to the queue of
    // somebody who is away.
    [SkippableFact]
    public async Task AnActiveDelegationRedirectsTheStep()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        Assert.True((await DelegateAsync(provider)).Succeeded);

        ApprovalStepDto step = Assert.Single((await StartAsync(provider)).Approval!.Steps);

        Assert.Equal(_coverId, step.ResolvedEmployeeId);
        Assert.Equal("Cara Okafor", step.ResolvedName);
    }

    // And the redirect explains itself. "Approved by Cara" on a step whose rule says
    // "line manager" reads as a bug or a permissions hole without this.
    [SkippableFact]
    public async Task TheStepRecordsWhoItWouldHaveGoneTo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);
        await DelegateAsync(provider);

        ApprovalStepDto step = Assert.Single((await StartAsync(provider)).Approval!.Steps);

        Assert.NotNull(step.Delegation);
        Assert.Equal(_managerId, step.Delegation!.FromEmployeeId);
        Assert.Equal("Ada Okafor", step.Delegation.FromName);
        Assert.Equal("Annual leave", step.Delegation.Reason);
    }

    // Cancelling must not make a past decision unexplainable, which is why the step keeps
    // its own copy of the period and the reason.
    [SkippableFact]
    public async Task CancellingLeavesAnAlreadyRedirectedStepIntact()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        Guid delegationId = (await DelegateAsync(provider)).Delegation!.Id;

        Guid approvalId = (await StartAsync(provider)).Approval!.Id;

        Assert.Equal(
            ApprovalDelegationOutcome.Succeeded,
            await As<IApprovalDelegationService>(provider, _managerUserId)
                .CancelAsync(delegationId));

        ApprovalDto approval = (await As<IApprovalService>(provider, _coverUserId)
            .GetAsync(approvalId))!;

        ApprovalStepDto step = Assert.Single(approval.Steps);

        Assert.Equal(_coverId, step.ResolvedEmployeeId);
        Assert.Equal("Ada Okafor", step.Delegation!.FromName);
    }

    // The delegate can actually act on it — a redirect that did not move the queue would
    // be decoration.
    [SkippableFact]
    public async Task TheDelegateCanDecideAndTheManagerCannot()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);
        await DelegateAsync(provider);

        Guid id = (await StartAsync(provider)).Approval!.Id;

        Assert.Single((await As<IApprovalService>(provider, _coverUserId)
            .MyQueueAsync(new PagedQuery())).Items);

        // It has left the manager's queue, because it is not waiting on them.
        Assert.Empty((await As<IApprovalService>(provider, _managerUserId)
            .MyQueueAsync(new PagedQuery())).Items);

        Assert.Equal(
            ApprovalOutcome.NotTheApprover,
            (await As<IApprovalService>(provider, _managerUserId)
                .DecideAsync(id, ApprovalStepStatus.Approved)).Outcome);

        Assert.True((await As<IApprovalService>(provider, _coverUserId)
            .DecideAsync(id, ApprovalStepStatus.Approved)).Succeeded);
    }

    // Outside its dates it does nothing. A delegation for next month must not redirect
    // today's work.
    [SkippableFact]
    public async Task ADelegationOutsideItsDatesDoesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        await DelegateAsync(provider, fromDay: 30, toDay: 37);

        ApprovalStepDto step = Assert.Single((await StartAsync(provider)).Approval!.Steps);

        Assert.Equal(_managerId, step.ResolvedEmployeeId);
        Assert.Null(step.Delegation);
    }

    // Inclusive at both ends. The half-open reading would leave the last day uncovered,
    // which is the day somebody is travelling back.
    [SkippableFact]
    public async Task TheLastDayIsCovered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        await DelegateAsync(provider, fromDay: -3, toDay: 0);

        ApprovalStepDto step = Assert.Single((await StartAsync(provider)).Approval!.Steps);

        Assert.Equal(_coverId, step.ResolvedEmployeeId);
    }

    [SkippableFact]
    public async Task DelegatingToYourselfIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ApprovalDelegationOutcome.DelegatingToSelf,
            (await DelegateAsync(provider, to: _managerId)).Outcome);
    }

    [SkippableFact]
    public async Task EndBeforeStartIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ApprovalDelegationOutcome.EndBeforeStart,
            (await DelegateAsync(provider, fromDay: 10, toDay: 2)).Outcome);
    }

    // Longer than a few months is a reassignment of duties, which is an org-chart change
    // rather than something to arrange from a side panel.
    [SkippableFact]
    public async Task AnIndefiniteDelegationIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ApprovalDelegationOutcome.TooLong,
            (await DelegateAsync(provider, fromDay: 0, toDay: DelegationRules.MaximumDays)).Outcome);
    }

    // Two arrangements covering the same day give the resolver nothing to choose between.
    [SkippableFact]
    public async Task OverlappingCoverIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await DelegateAsync(provider, fromDay: 0, toDay: 7)).Succeeded);

        Assert.Equal(
            ApprovalDelegationOutcome.Overlapping,
            (await DelegateAsync(provider, fromDay: 5, toDay: 12)).Outcome);

        // Adjacent is fine — it is overlap that is ambiguous, not proximity.
        Assert.True((await DelegateAsync(provider, fromDay: 8, toDay: 15)).Succeeded);
    }

    // Routing a queue to somebody who has left is how it ages for a month before anybody
    // notices.
    [SkippableFact]
    public async Task DelegatingToALeaverIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await using (MoteeDbContext context = fixture.CreateContext(_tenantId))
        {
            Employee cover = await context.Employees.FirstAsync(e => e.Id == _coverId);
            cover.Status = EmployeeStatus.Inactive;
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            ApprovalDelegationOutcome.DelegateUnavailable,
            (await DelegateAsync(provider)).Outcome);
    }

    // Only the person who arranged it. An administrator cancelling somebody's cover while
    // they are away is how a queue silently stops being watched.
    [SkippableFact]
    public async Task SomebodyElseCannotCancelIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await DelegateAsync(provider)).Delegation!.Id;

        Assert.Equal(
            ApprovalDelegationOutcome.NotTheirs,
            await As<IApprovalDelegationService>(provider, _coverUserId).CancelAsync(id));
    }

    // Not recursive. If Ada delegates to Cara and Cara delegates to somebody else, the
    // step stops at Cara — a chain is how work reaches somebody three steps removed with
    // no idea why they have it.
    [SkippableFact]
    public async Task DelegationDoesNotChain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        Guid thirdId = Guid.NewGuid();
        Guid thirdUserId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Add(seed, thirdId, thirdUserId, "Emeka");
            await seed.SaveChangesAsync();
        }

        await DelegateAsync(provider);

        await As<IApprovalDelegationService>(provider, _coverUserId).CreateAsync(
            new ApprovalDelegationRequest
            {
                DelegateEmployeeId = thirdId,
                StartDate = Today,
                EndDate = Today.AddDays(7),
            });

        ApprovalStepDto step = Assert.Single((await StartAsync(provider)).Approval!.Steps);

        Assert.Equal(_coverId, step.ResolvedEmployeeId);
    }

    // Their own panel shows their own arrangements and nobody else's.
    [SkippableFact]
    public async Task ThePanelShowsOnlyYourOwnArrangements()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await DelegateAsync(provider);

        Assert.Single(await As<IApprovalDelegationService>(provider, _managerUserId).MineAsync());

        // Cara is the delegate, not the delegator — this is not hers.
        Assert.Empty(await As<IApprovalDelegationService>(provider, _coverUserId).MineAsync());

        // But an administrator sees who is covering for whom.
        Assert.Single(await As<IApprovalDelegationService>(provider, _managerUserId).ActiveAsync());
    }
}
