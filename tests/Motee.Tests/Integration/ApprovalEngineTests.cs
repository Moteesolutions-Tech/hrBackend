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

// The engine end to end: a template, a run against a real employee, and decisions taken
// by the people it actually resolved to.
//
// The two state machines are covered without a database in ApprovalLifecycleTests. These
// cover what the machines mean once there are people, accounts and an org chart.
[Collection(PostgresCollection.Name)]
public class ApprovalEngineTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _subjectId;
    private Guid _managerId;
    private Guid _managerUserId;
    private Guid _headId;
    private Guid _headUserId;

    // A two-step chain — line manager, then department head — against an employee who
    // has both. The ordinary case.
    private async Task<ServiceProvider> ArrangeAsync(bool withManager = true, bool withHead = true)
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();

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

        _managerId = Guid.NewGuid();
        _managerUserId = Guid.NewGuid();
        _headId = Guid.NewGuid();
        _headUserId = Guid.NewGuid();
        _subjectId = Guid.NewGuid();

        Guid departmentId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Add(seed, _managerId, _managerUserId, "Ada");
            Add(seed, _headId, _headUserId, "Cara");

            seed.Departments.Add(new Department
            {
                Id = departmentId,
                TenantId = _tenantId,
                Name = "Engineering",
                Code = "ENG",
                HeadEmployeeId = withHead ? _headId : null,
                Status = DepartmentStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            seed.Employees.Add(new Employee
            {
                Id = _subjectId,
                TenantId = _tenantId,
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
                ManagerId = withManager ? _managerId : null,
                DepartmentId = departmentId,
                Status = EmployeeStatus.Active,
                OnboardingMethod = OnboardingMethod.Manual,
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
            OnboardingMethod = OnboardingMethod.Manual,
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

    private static async Task<Guid> TemplateAsync(
        ServiceProvider provider,
        bool headRequired = true)
    {
        IApprovalTemplateService templates = provider.CreateScope().ServiceProvider
            .GetRequiredService<IApprovalTemplateService>();

        ApprovalTemplateResult created = await templates.CreateAsync(new ApprovalTemplateRequest
        {
            DocumentType = ApprovalDocumentTypes.Onboarding,
            Name = "Standard onboarding",
            IsDefault = true,
            Steps =
            [
                new ApprovalTemplateStepRequest
                {
                    Label = "Line manager approval",
                    Approver = ApproverResolver.LineManager,
                    Required = true,
                },
                new ApprovalTemplateStepRequest
                {
                    Label = "Department head approval",
                    Approver = ApproverResolver.DepartmentHead,
                    Required = headRequired,
                },
            ],
        });

        Assert.True(created.Succeeded);

        return created.Template!.Id;
    }

    // Acts as a given user, because who may decide a step is the point.
    private static IApprovalService As(ServiceProvider provider, Guid userId)
    {
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

        return provider.CreateScope().ServiceProvider.GetRequiredService<IApprovalService>();
    }

    private Task<ApprovalResult> StartAsync(ServiceProvider provider) =>
        As(provider, _managerUserId).StartAsync(new StartApprovalRequest
        {
            DocumentType = ApprovalDocumentTypes.Onboarding,
            SubjectType = "OnboardingRecord",
            SubjectId = Guid.NewGuid(),
            SubjectEmployeeId = _subjectId,
        });

    [SkippableFact]
    public async Task StartingResolvesEveryStepAndWaitsOnTheFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        ApprovalDto approval = (await StartAsync(provider)).Approval!;

        Assert.Equal(ApprovalStatus.InProgress, approval.Status);
        Assert.Equal(2, approval.Steps.Count);

        // Resolved at submission, both of them — so the chain knows who it is waiting on
        // before anybody has acted.
        Assert.Equal(_managerId, approval.Steps[0].ResolvedEmployeeId);
        Assert.Equal(_headId, approval.Steps[1].ResolvedEmployeeId);

        // But only the first is in front of anyone.
        Assert.Equal("Line manager approval", approval.CurrentStep!.Label);
    }

    [SkippableFact]
    public async Task ApprovingEveryStepApprovesTheChain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        Guid id = (await StartAsync(provider)).Approval!.Id;

        ApprovalResult first = await As(provider, _managerUserId)
            .DecideAsync(id, ApprovalStepStatus.Approved, "Looks right");

        // Still running: the head has not seen it.
        Assert.Equal(ApprovalStatus.InProgress, first.Approval!.Status);
        Assert.Equal("Department head approval", first.Approval.CurrentStep!.Label);

        ApprovalResult second = await As(provider, _headUserId)
            .DecideAsync(id, ApprovalStepStatus.Approved);

        Assert.Equal(ApprovalStatus.Approved, second.Approval!.Status);
        Assert.Null(second.Approval.CurrentStep);
    }

    // Without this, anyone holding the module permission could approve anyone's step and
    // the order the template describes would mean nothing.
    [SkippableFact]
    public async Task OnlyThePersonItIsWaitingOnCanDecide()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        Guid id = (await StartAsync(provider)).Approval!.Id;

        // The head is second in the chain and tries to go first.
        ApprovalResult early = await As(provider, _headUserId)
            .DecideAsync(id, ApprovalStepStatus.Approved);

        Assert.Equal(ApprovalOutcome.NotTheApprover, early.Outcome);
    }

    [SkippableFact]
    public async Task ARejectionEndsTheChainWhereItHappens()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        Guid id = (await StartAsync(provider)).Approval!.Id;

        ApprovalResult rejected = await As(provider, _managerUserId)
            .DecideAsync(id, ApprovalStepStatus.Rejected, "Start date is wrong");

        Assert.Equal(ApprovalStatus.Rejected, rejected.Approval!.Status);

        // The head is never asked.
        Assert.Equal(ApprovalStepStatus.Pending, rejected.Approval.Steps[1].Status);
    }

    // The approvals already given were given to a different document — whoever signed
    // the original never saw what finally went through.
    [SkippableFact]
    public async Task ResubmittingStartsTheWholeChainAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        Guid id = (await StartAsync(provider)).Approval!.Id;

        await As(provider, _managerUserId).DecideAsync(id, ApprovalStepStatus.Approved);

        ApprovalResult returned = await As(provider, _headUserId)
            .DecideAsync(id, ApprovalStepStatus.Returned, "Attach the contract");

        Assert.Equal(ApprovalStatus.Returned, returned.Approval!.Status);

        ApprovalResult resubmitted = await As(provider, _managerUserId).ResubmitAsync(id);

        Assert.Equal(ApprovalStatus.InProgress, resubmitted.Approval!.Status);
        Assert.Equal(2, resubmitted.Approval.Round);

        // Including the one already approved — it is back with the line manager.
        Assert.All(resubmitted.Approval.Steps,
            step => Assert.Equal(ApprovalStepStatus.Pending, step.Status));

        Assert.Equal("Line manager approval", resubmitted.Approval.CurrentStep!.Label);
    }

    // An optional step nobody could be found for must not block, and must say why it was
    // passed over rather than silently vanishing.
    [SkippableFact]
    public async Task AnOptionalStepWithNobodyToAskIsSkippedWithAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(withHead: false);
        await TemplateAsync(provider, headRequired: false);

        ApprovalDto approval = (await StartAsync(provider)).Approval!;

        Assert.Equal(ApprovalStepStatus.Skipped, approval.Steps[1].Status);
        Assert.Contains("no head", approval.Steps[1].SkippedReason!, StringComparison.OrdinalIgnoreCase);

        // And approving the first step now finishes the whole thing, because the second
        // was already settled.
        ApprovalResult decided = await As(provider, _managerUserId)
            .DecideAsync(approval.Id, ApprovalStepStatus.Approved);

        Assert.Equal(ApprovalStatus.Approved, decided.Approval!.Status);
    }

    // A required step with nobody to ask must never quietly approve itself. It stays
    // pending, carrying the reason, so somebody fixes the org chart.
    [SkippableFact]
    public async Task ARequiredStepWithNobodyToAskBlocksAndSaysWhy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(withHead: false);
        await TemplateAsync(provider, headRequired: true);

        Guid id = (await StartAsync(provider)).Approval!.Id;

        ApprovalResult decided = await As(provider, _managerUserId)
            .DecideAsync(id, ApprovalStepStatus.Approved);

        // Not approved: the head step is still waiting on somebody who does not exist.
        Assert.Equal(ApprovalStatus.InProgress, decided.Approval!.Status);

        ApprovalStepDto blocked = decided.Approval.Steps[1];

        Assert.Equal(ApprovalStepStatus.Pending, blocked.Status);
        Assert.Contains("no head", blocked.SkippedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task TheQueueShowsWhatIsWaitingOnMe()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        await StartAsync(provider);

        // With the line manager, not the head.
        Assert.Equal(1, (await As(provider, _managerUserId).MyQueueAsync(new PagedQuery())).TotalItems);
        Assert.Equal(0, (await As(provider, _headUserId).MyQueueAsync(new PagedQuery())).TotalItems);
    }

    // The instance says where it got to; the history says how it got there, which is the
    // half an auditor asks for.
    [SkippableFact]
    public async Task EveryDecisionIsRecorded()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        Guid id = (await StartAsync(provider)).Approval!.Id;

        await As(provider, _managerUserId).DecideAsync(id, ApprovalStepStatus.Approved, "Fine");

        ApprovalResult returned = await As(provider, _headUserId)
            .DecideAsync(id, ApprovalStepStatus.Returned, "Missing contract");

        IReadOnlyList<ApprovalEventDto> history = returned.Approval!.History;

        Assert.Equal(
            [ApprovalEventTypes.Submitted, ApprovalEventTypes.Approved, ApprovalEventTypes.Returned],
            history.Select(entry => entry.Type));

        Assert.Equal("Missing contract", history[^1].Note);
    }

    // The snapshot. Editing a template must not reach an approval already running, or an
    // approval could grow a step halfway through and become unaccountable.
    [SkippableFact]
    public async Task EditingATemplateDoesNotChangeARunningApproval()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        Guid templateId = await TemplateAsync(provider);

        Guid id = (await StartAsync(provider)).Approval!.Id;

        IApprovalTemplateService templates = provider.CreateScope().ServiceProvider
            .GetRequiredService<IApprovalTemplateService>();

        await templates.UpdateAsync(templateId, new ApprovalTemplateRequest
        {
            DocumentType = ApprovalDocumentTypes.Onboarding,
            Name = "Standard onboarding",
            IsDefault = true,
            Steps =
            [
                new ApprovalTemplateStepRequest
                {
                    Label = "Renamed and now the only step",
                    Approver = ApproverResolver.LineManager,
                    Required = true,
                },
            ],
        });

        ApprovalDto running = (await As(provider, _managerUserId).GetAsync(id))!;

        Assert.Equal(2, running.Steps.Count);
        Assert.Equal("Line manager approval", running.Steps[0].Label);
    }

    // A template that approvals have run against is the record of decisions people made.
    // Deleting it would leave that history pointing at nothing.
    [SkippableFact]
    public async Task ATemplateWithApprovalsCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        Guid templateId = await TemplateAsync(provider);

        await StartAsync(provider);

        IApprovalTemplateService templates = provider.CreateScope().ServiceProvider
            .GetRequiredService<IApprovalTemplateService>();

        Assert.Equal(ApprovalTemplateOutcome.InUse, await templates.DeleteAsync(templateId));
    }

    // Two defaults would make "start the default chain" ambiguous, and the module
    // starting it has no way to choose.
    [SkippableFact]
    public async Task SettingANewDefaultClearsTheOldOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        Guid firstId = await TemplateAsync(provider);

        IApprovalTemplateService templates = provider.CreateScope().ServiceProvider
            .GetRequiredService<IApprovalTemplateService>();

        ApprovalTemplateResult second = await templates.CreateAsync(new ApprovalTemplateRequest
        {
            DocumentType = ApprovalDocumentTypes.Onboarding,
            Name = "Contractor onboarding",
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

        Assert.True(second.Succeeded);
        Assert.True(second.Template!.IsDefault);
        Assert.False((await templates.GetAsync(firstId))!.IsDefault);
    }

    [SkippableFact]
    public async Task StartingWithNoTemplateConfiguredSaysSo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalResult result = await StartAsync(provider);

        Assert.Equal(ApprovalOutcome.TemplateNotFound, result.Outcome);
    }
}
