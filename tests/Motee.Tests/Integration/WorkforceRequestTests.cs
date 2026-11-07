using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Headcount;
using Motee.Application.Organisation;
using Motee.Application.Recruitment;
using Motee.Application.Workforce;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;
using Motee.Domain.Identity;
using Motee.Domain.Employees;
using Motee.Domain.Headcount;
using Motee.Domain.Organisation;
using Motee.Domain.Workforce;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Workforce requests, and the chain they complete.
//
// The last test here is the one this module was built for: a gap on the
// headcount plan, through a request, through the tenant's own approval
// chain, into a vacancy that the recruitment module can then run. Every link
// existed except the first, so the chain started at the vacancy and nobody
// could answer who authorised it.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class WorkforceRequestTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 11, 2).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _engineeringId;

    private Guid _caraId;
    private Guid _bolaId;

    private Guid _caraUserId;

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

        _engineeringId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        // Cara manages Bola. Bola raises the requests; Cara is the line
        // manager the seeded chain resolves to.
        _caraId = await HireAsync(provider, "Cara");
        _bolaId = await HireAsync(provider, "Bola", managerId: _caraId);

        _caraUserId = await AccountAsync(_caraId, "cara");
        Guid bolaUserId = await AccountAsync(_bolaId, "bola");

        // The seeded headcount chain ends at whoever holds Payroll & Finance,
        // because a headcount request is a budget commitment. Cara wears that
        // hat too, so the chain has somebody to resolve its last step to.
        await HoldsFinanceAsync(_caraUserId);

        ActAs(provider, bolaUserId);

        return provider;
    }

    private async Task HoldsFinanceAsync(Guid userId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid financeLevelId = await seed.AccessLevels
            .Where(level => level.TemplateSlug == Roles.ToSlug(Role.Finance))
            .Select(level => level.Id)
            .FirstAsync();

        seed.UserAccessLevels.Add(new UserAccessLevel
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            UserId = userId,
            AccessLevelId = financeLevelId,
            AssignedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid? managerId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _engineeringId,
            ManagerId = managerId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task<Guid> AccountAsync(Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

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

        return userId;
    }

    private static void ActAs(ServiceProvider provider, Guid userId) =>
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IWorkforceRequestService Workforce(ServiceProvider provider) =>
        Resolve<IWorkforceRequestService>(provider);

    private WorkforceRequestRequest Asking(
        string position = "Backend engineer",
        int hires = 1,
        string? costCentre = "ENG-01",
        Guid? departmentId = null,
        string justification = "Two leavers and a roadmap we have committed to.") => new()
        {
            DepartmentId = departmentId ?? _engineeringId,
            Position = position,
            NumberOfHires = hires,
            Justification = justification,
            CostCentreCode = costCentre,
            BudgetEstimate = 120_000m,
            Currency = "NGN",
            Urgency = WorkforceUrgency.High,
            ExpectedStartDate = new DateOnly(2027, 1, 11),
        };

    private static async Task<WorkforceRequestDto> DraftAsync(
        ServiceProvider provider,
        WorkforceRequestRequest request)
    {
        WorkforceRequestResult result = await Workforce(provider).CreateAsync(request);

        Assert.True(result.Succeeded, result.Outcome.ToString());

        return result.Request!;
    }

    // Walks the seeded chain to approved by deciding every outstanding step as
    // whoever it resolved to. Written as a loop because the chain's length
    // depends on what the tenant has configured.
    private async Task ApproveChainAsync(ServiceProvider provider, Guid requestId)
    {
        for (int round = 0; round < 6; round++)
        {
            WorkforceRequestDto current = (await Workforce(provider).GetAsync(requestId))!;

            if (current.Status != WorkforceRequestStatus.PendingApproval)
            {
                return;
            }

            ApprovalDto chain = (await Resolve<IApprovalService>(provider)
                .GetAsync(current.ApprovalInstanceId!.Value))!;

            ApprovalStepDto step = chain.CurrentStep
                ?? throw new InvalidOperationException("Nothing to decide.");

            // Act as whoever the step resolved to, which is what the engine
            // checks before accepting a decision. A positional step names a
            // person; a role step names a level, and anybody holding it can
            // decide.
            if (step.ResolvedEmployeeId is Guid approver)
            {
                ActAs(provider, await UserForAsync(approver));
            }
            else if (step.ResolvedRoleId is Guid roleId)
            {
                ActAs(provider, await UserHoldingAsync(roleId));
            }

            ApprovalResult decided = await Resolve<IApprovalService>(provider).DecideAsync(
                chain.Id, ApprovalStepStatus.Approved, "Agreed.");

            Assert.True(decided.Succeeded, decided.Outcome.ToString());
        }
    }

    private async Task<Guid> UserHoldingAsync(Guid accessLevelId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        return await seed.UserAccessLevels
            .Where(assignment => assignment.AccessLevelId == accessLevelId)
            .Select(assignment => assignment.UserId)
            .FirstAsync();
    }

    private async Task<Guid> UserForAsync(Guid employeeId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        return await seed.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();
    }

    // ── drafting ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewRequestIsADraftWithNoReferenceYet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());

        Assert.Equal(WorkforceRequestStatus.Draft, draft.Status);
        Assert.Null(draft.Reference);
        Assert.Null(draft.ApprovalInstanceId);
        Assert.Equal("Engineering", draft.DepartmentName);
        Assert.Equal("Bola Okafor", draft.RequestedByName);
    }

    [SkippableFact]
    public async Task ADraftIsEditedFreely()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());

        WorkforceRequestResult updated = await Workforce(provider).UpdateAsync(
            draft.Id, Asking(position: "Staff engineer", hires: 2));

        Assert.True(updated.Succeeded, updated.Outcome.ToString());
        Assert.Equal("Staff engineer", updated.Request!.Position);
        Assert.Equal(2, updated.Request.NumberOfHires);
    }

    [SkippableFact]
    public async Task ADraftIsDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());

        Assert.Equal(
            WorkforceRequestOutcome.Succeeded,
            await Workforce(provider).DeleteAsync(draft.Id));

        Assert.Null(await Workforce(provider).GetAsync(draft.Id));
    }

    [SkippableFact]
    public async Task ARequestForADepartmentThatDoesNotExistIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            WorkforceRequestOutcome.DepartmentNotFound,
            (await Workforce(provider).CreateAsync(
                Asking(departmentId: Guid.NewGuid()))).Outcome);
    }

    // ── submitting ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SubmittingStartsTheTenantsOwnChainAndIssuesAReference()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        WorkforceRequestResult sent = await Workforce(provider).SubmitAsync(draft.Id);

        Assert.True(sent.Succeeded, sent.Outcome.ToString());
        Assert.Equal(WorkforceRequestStatus.PendingApproval, sent.Request!.Status);
        Assert.Equal("WFR-2026-00001", sent.Request.Reference);
        Assert.NotNull(sent.Request.ApprovalInstanceId);

        // The first step of the seeded chain is the line manager, so the
        // request can say who it is sitting with.
        Assert.Equal("Cara Okafor", sent.Request.WaitingOn);
    }

    [SkippableFact]
    public async Task ReferencesRunInSequence()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto first = await DraftAsync(provider, Asking());
        WorkforceRequestDto second = await DraftAsync(provider, Asking(position: "SRE"));

        Assert.Equal(
            "WFR-2026-00001", (await Workforce(provider).SubmitAsync(first.Id)).Request!.Reference);
        Assert.Equal(
            "WFR-2026-00002", (await Workforce(provider).SubmitAsync(second.Id)).Request!.Reference);
    }

    [SkippableFact]
    public async Task ARequestWithNothingToChargeIsNotSubmitted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking(costCentre: null));
        WorkforceRequestResult sent = await Workforce(provider).SubmitAsync(draft.Id);

        Assert.Equal(WorkforceRequestOutcome.Refused, sent.Outcome);
        Assert.Equal(WorkforceRefusal.CostCentreRequired, sent.Refusal);
    }

    [SkippableFact]
    public async Task ARequestWithNoCaseIsNotSubmitted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking(justification: "  "));

        Assert.Equal(
            WorkforceRefusal.JustificationRequired,
            (await Workforce(provider).SubmitAsync(draft.Id)).Refusal);
    }

    [SkippableFact]
    public async Task ARequestAlreadyWithApproversIsNotSubmittedTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        await Workforce(provider).SubmitAsync(draft.Id);

        Assert.Equal(
            WorkforceRefusal.AlreadySubmitted,
            (await Workforce(provider).SubmitAsync(draft.Id)).Refusal);
    }

    // An approver reading a request that changes under them is approving
    // something nobody saw.
    [SkippableFact]
    public async Task ARequestWithApproversCannotBeEdited()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        await Workforce(provider).SubmitAsync(draft.Id);

        Assert.Equal(
            WorkforceRefusal.AlreadySubmitted,
            (await Workforce(provider).UpdateAsync(draft.Id, Asking(hires: 9))).Refusal);
    }

    // Once it has been in front of an approver it is part of the
    // authorisation record.
    [SkippableFact]
    public async Task ASubmittedRequestCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        await Workforce(provider).SubmitAsync(draft.Id);

        Assert.Equal(
            WorkforceRequestOutcome.Refused,
            await Workforce(provider).DeleteAsync(draft.Id));
    }

    // ── the chain decides the status ────────────────────────────────────────

    [SkippableFact]
    public async Task ApprovingTheChainMakesTheRequestApproved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        await Workforce(provider).SubmitAsync(draft.Id);

        await ApproveChainAsync(provider, draft.Id);

        WorkforceRequestDto approved = (await Workforce(provider).GetAsync(draft.Id))!;

        Assert.Equal(WorkforceRequestStatus.Approved, approved.Status);

        // Nothing is waiting any more, so nothing is named.
        Assert.Null(approved.WaitingOn);
    }

    [SkippableFact]
    public async Task RejectingTheChainMakesTheRequestRejected()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        WorkforceRequestDto sent = (await Workforce(provider).SubmitAsync(draft.Id)).Request!;

        ApprovalDto chain = (await Resolve<IApprovalService>(provider)
            .GetAsync(sent.ApprovalInstanceId!.Value))!;

        ActAs(provider, _caraUserId);

        Assert.True((await Resolve<IApprovalService>(provider).DecideAsync(
            chain.Id, ApprovalStepStatus.Rejected, "No budget this quarter.")).Succeeded);

        Assert.Equal(
            WorkforceRequestStatus.Rejected,
            (await Workforce(provider).GetAsync(draft.Id))!.Status);
    }

    // A rejected request is the requester's again: they rework the case and
    // send it back, keeping the reference Finance already has.
    [SkippableFact]
    public async Task ARejectedRequestIsRewordedAndSentAgainKeepingItsReference()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        WorkforceRequestDto sent = (await Workforce(provider).SubmitAsync(draft.Id)).Request!;

        ApprovalDto chain = (await Resolve<IApprovalService>(provider)
            .GetAsync(sent.ApprovalInstanceId!.Value))!;

        ActAs(provider, _caraUserId);
        await Resolve<IApprovalService>(provider).DecideAsync(
            chain.Id, ApprovalStepStatus.Rejected, "Make the case.");

        WorkforceRequestResult reworked = await Workforce(provider).UpdateAsync(
            draft.Id, Asking(justification: "Revenue is up 40% and support is drowning."));

        Assert.True(reworked.Succeeded, reworked.Outcome.ToString());

        WorkforceRequestResult again = await Workforce(provider).SubmitAsync(draft.Id);

        Assert.True(again.Succeeded, again.Outcome.ToString());
        Assert.Equal("WFR-2026-00001", again.Request!.Reference);
        Assert.Equal(WorkforceRequestStatus.PendingApproval, again.Request.Status);
    }

    // ── withdrawing ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task WithdrawingARequestTakesItsChainOutOfEverybodysQueue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        WorkforceRequestDto sent = (await Workforce(provider).SubmitAsync(draft.Id)).Request!;

        WorkforceRequestResult withdrawn = await Workforce(provider).CancelAsync(
            draft.Id, "Reorganised the team instead.");

        Assert.True(withdrawn.Succeeded, withdrawn.Outcome.ToString());
        Assert.Equal(WorkforceRequestStatus.Cancelled, withdrawn.Request!.Status);

        ApprovalDto chain = (await Resolve<IApprovalService>(provider)
            .GetAsync(sent.ApprovalInstanceId!.Value))!;

        Assert.Equal(ApprovalStatus.Cancelled, chain.Status);
    }

    [SkippableFact]
    public async Task WithdrawingNeedsAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());

        Assert.Equal(
            WorkforceRequestOutcome.ReasonRequired,
            (await Workforce(provider).CancelAsync(draft.Id, " ")).Outcome);
    }

    // ── becoming a vacancy ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnUnapprovedRequestDoesNotBecomeAVacancy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        await Workforce(provider).SubmitAsync(draft.Id);

        Assert.Equal(
            WorkforceRefusal.NotApproved,
            (await Workforce(provider).ConvertToRequisitionAsync(draft.Id)).Refusal);
    }

    [SkippableFact]
    public async Task ARequestOnlyBecomesAVacancyOnce()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking());
        await Workforce(provider).SubmitAsync(draft.Id);
        await ApproveChainAsync(provider, draft.Id);

        Assert.True((await Workforce(provider).ConvertToRequisitionAsync(draft.Id)).Succeeded);

        Assert.Equal(
            WorkforceRefusal.AlreadyConverted,
            (await Workforce(provider).ConvertToRequisitionAsync(draft.Id)).Refusal);
    }

    // The test this module exists for: headcount gap → request → the
    // tenant's own approval chain → vacancy the recruitment module can run.
    [SkippableFact]
    public async Task AGapBecomesARequestBecomesAnApprovalBecomesAVacancy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // A gap: two people in Engineering, a target of four.
        await Resolve<IHeadcountService>(provider).SetTargetAsync(new HeadcountPlanRequest
        {
            DepartmentId = _engineeringId,
            Period = "Q4 2026",
            Target = 4,
        });

        HeadcountPlanRowDto gap = Assert.Single(
            (await Resolve<IHeadcountService>(provider).BoardAsync("Q4 2026")).Rows,
            row => row.DepartmentId == _engineeringId);

        Assert.Equal(-2, gap.Gap);

        // The gap report's own advice, with nothing raised against it yet.
        Assert.Equal("Create Workforce Request", gap.RecommendedAction);

        // So somebody raises one.
        WorkforceRequestDto draft = await DraftAsync(provider, Asking(hires: 2));
        await Workforce(provider).SubmitAsync(draft.Id);
        await ApproveChainAsync(provider, draft.Id);

        WorkforceRequestResult converted =
            await Workforce(provider).ConvertToRequisitionAsync(draft.Id);

        Assert.True(converted.Succeeded, converted.Outcome.ToString());
        Assert.Equal(WorkforceRequestStatus.Converted, converted.Request!.Status);
        Assert.NotNull(converted.Request.RequisitionId);

        // The vacancy is a real one, carrying what was authorised.
        RequisitionDto vacancy = (await Resolve<IRequisitionService>(provider)
            .GetAsync(converted.Request.RequisitionId!.Value))!;

        Assert.Equal("Backend engineer", vacancy.PositionTitle);
        Assert.Equal(_engineeringId, vacancy.DepartmentId);
        Assert.Equal(2, vacancy.Openings);
        Assert.Equal(new DateOnly(2027, 1, 11), vacancy.TargetStartDate);
        Assert.Equal(_bolaId, vacancy.HiringManagerId);

        // And the headcount board now says hiring is under way rather than
        // telling somebody to raise a request that already exists.
        HeadcountPlanRowDto after = Assert.Single(
            (await Resolve<IHeadcountService>(provider).BoardAsync("Q4 2026")).Rows,
            row => row.DepartmentId == _engineeringId);

        Assert.Equal(RecruitmentPipelineStage.RequestRaised, after.RecruitmentStage);
    }

    // ── the queue ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheQueueNarrowsByStatusEvenThoughStatusIsDerived()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto pending = await DraftAsync(provider, Asking());
        await Workforce(provider).SubmitAsync(pending.Id);

        await DraftAsync(provider, Asking(position: "SRE"));

        PagedResult<WorkforceRequestDto> waiting = await Workforce(provider).ListAsync(
            new WorkforceRequestQuery { Status = WorkforceRequestStatus.PendingApproval });

        Assert.Equal(pending.Id, Assert.Single(waiting.Items).Id);

        PagedResult<WorkforceRequestDto> drafts = await Workforce(provider).ListAsync(
            new WorkforceRequestQuery { Status = WorkforceRequestStatus.Draft });

        Assert.Equal("SRE", Assert.Single(drafts.Items).Position);
    }

    [SkippableFact]
    public async Task TheQueueSearchesByPositionAndReference()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkforceRequestDto draft = await DraftAsync(provider, Asking(position: "Data engineer"));
        await Workforce(provider).SubmitAsync(draft.Id);
        await DraftAsync(provider, Asking(position: "Recruiter"));

        Assert.Equal(
            "Data engineer",
            Assert.Single((await Workforce(provider).ListAsync(
                new WorkforceRequestQuery { Search = "data" })).Items).Position);

        Assert.Equal(
            "Data engineer",
            Assert.Single((await Workforce(provider).ListAsync(
                new WorkforceRequestQuery { Search = "WFR-2026-00001" })).Items).Position);
    }

    [SkippableFact]
    public async Task TheQueueNarrowsToOneDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid sales = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        await DraftAsync(provider, Asking());
        await DraftAsync(provider, Asking(position: "AE", departmentId: sales));

        Assert.Equal(
            "AE",
            Assert.Single((await Workforce(provider).ListAsync(
                new WorkforceRequestQuery { DepartmentId = sales })).Items).Position);
    }
}
