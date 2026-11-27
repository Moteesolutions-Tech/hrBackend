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
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Two lists the engine never had: everything in the tenant, and everything one person
// raised.
//
// The engine already answered "what is waiting on me" and "what is running against this
// record". Neither of those is the Submissions screen, which is a register — every
// request, whoever raised it, whatever became of it. Without it the only way to see a
// submission was to be its next approver, so an approval nobody was waiting on was
// invisible to everybody including the person who sent it.
[Collection(PostgresCollection.Name)]
public class SubmissionsRegisterTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _subjectId;
    private Guid _managerId;
    private Guid _managerUserId;
    private Guid _headId;
    private Guid _headUserId;
    private Guid _departmentId;

    private async Task<ServiceProvider> ArrangeAsync()
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
        _departmentId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Add(seed, _managerId, _managerUserId, "Ada");
            Add(seed, _headId, _headUserId, "Cara");

            seed.Departments.Add(new Department
            {
                Id = _departmentId,
                TenantId = _tenantId,
                Name = "Engineering",
                Code = "ENG",
                HeadEmployeeId = _headId,
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
                ManagerId = _managerId,
                DepartmentId = _departmentId,
                Status = EmployeeStatus.Active,
                OnboardingMethod = OnboardingMethod.Manual,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        await TemplateAsync(provider, ApprovalDocumentTypes.Onboarding);
        await TemplateAsync(provider, ApprovalDocumentTypes.LeaveRequest);

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

    private static async Task TemplateAsync(ServiceProvider provider, string documentType)
    {
        IApprovalTemplateService templates = provider.CreateScope().ServiceProvider
            .GetRequiredService<IApprovalTemplateService>();

        ApprovalTemplateResult created = await templates.CreateAsync(new ApprovalTemplateRequest
        {
            DocumentType = documentType,
            Name = $"Standard {documentType}",
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

        Assert.True(created.Succeeded);
    }

    private static IApprovalService As(ServiceProvider provider, Guid userId)
    {
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

        return provider.CreateScope().ServiceProvider.GetRequiredService<IApprovalService>();
    }

    private Task<ApprovalResult> StartAsync(
        ServiceProvider provider,
        Guid byUserId,
        string documentType = ApprovalDocumentTypes.Onboarding,
        string subjectType = "OnboardingRecord") =>
        As(provider, byUserId).StartAsync(new StartApprovalRequest
        {
            DocumentType = documentType,
            SubjectType = subjectType,
            SubjectId = Guid.NewGuid(),
            SubjectEmployeeId = _subjectId,
        });

    // ── the register ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheRegisterListsEveryApprovalWhoeverRaisedIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);
        Assert.True((await StartAsync(provider, _headUserId)).Succeeded);

        // Asked as Cara, who raised one of the two. A queue would show her nothing here —
        // neither is waiting on her, both are with Ada.
        PagedResult<ApprovalDto> register =
            await As(provider, _headUserId).ListAsync(new ApprovalQuery());

        Assert.Equal(2, register.TotalItems);
        Assert.Equal(2, register.Items.Count);
    }

    [SkippableFact]
    public async Task TheRegisterSaysWhoRaisedEachOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _headUserId)).Succeeded);

        ApprovalDto only = Assert.Single(
            (await As(provider, _managerUserId).ListAsync(new ApprovalQuery())).Items);

        // Without this the register has a row per submission and no column for the
        // person who made it, which is the one thing it exists to show.
        Assert.Equal(_headUserId, only.SubmittedByUserId);
        Assert.Equal(_headId, only.SubmittedByEmployeeId);
        Assert.Equal("Cara Okafor", only.SubmittedByName);
    }

    [SkippableFact]
    public async Task TheRegisterIsNewestFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto older = (await StartAsync(provider, _managerUserId)).Approval!;

        _clock.Advance(TimeSpan.FromHours(2));

        ApprovalDto newer = (await StartAsync(provider, _managerUserId)).Approval!;

        PagedResult<ApprovalDto> register =
            await As(provider, _managerUserId).ListAsync(new ApprovalQuery());

        // The opposite order to the queue. A queue is worked oldest first; a register is
        // read newest first, because the question is what just happened.
        Assert.Equal(newer.Id, register.Items[0].Id);
        Assert.Equal(older.Id, register.Items[1].Id);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersByStatus()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto cancelled = (await StartAsync(provider, _managerUserId)).Approval!;
        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        Assert.True((await As(provider, _managerUserId)
            .CancelAsync(cancelled.Id, "Withdrawn")).Succeeded);

        PagedResult<ApprovalDto> live = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { Status = ApprovalStatus.InProgress });

        Assert.Single(live.Items);
        Assert.DoesNotContain(live.Items, item => item.Id == cancelled.Id);

        PagedResult<ApprovalDto> dead = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { Status = ApprovalStatus.Cancelled });

        Assert.Equal(cancelled.Id, Assert.Single(dead.Items).Id);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersByDocumentType()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        Assert.True((await StartAsync(
            provider,
            _managerUserId,
            ApprovalDocumentTypes.LeaveRequest,
            "LeaveRequest")).Succeeded);

        PagedResult<ApprovalDto> leave = await As(provider, _managerUserId).ListAsync(
            new ApprovalQuery { DocumentType = ApprovalDocumentTypes.LeaveRequest });

        Assert.Equal(
            ApprovalDocumentTypes.LeaveRequest,
            Assert.Single(leave.Items).DocumentType);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersByWhoRaisedIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);
        Assert.True((await StartAsync(provider, _headUserId)).Succeeded);

        // Named by employee rather than by user, because that is who the screen picks
        // from. The engine stores the account that submitted; this resolves across.
        PagedResult<ApprovalDto> carasOwn = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { RequesterEmployeeId = _headId });

        Assert.Equal(_headUserId, Assert.Single(carasOwn.Items).SubmittedByUserId);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersBySubject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        PagedResult<ApprovalDto> aboutBen = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { SubjectEmployeeId = _subjectId });

        Assert.Single(aboutBen.Items);

        PagedResult<ApprovalDto> aboutCara = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { SubjectEmployeeId = _headId });

        Assert.Empty(aboutCara.Items);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersByDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        _clock.Advance(TimeSpan.FromDays(3));

        ApprovalDto later = (await StartAsync(provider, _managerUserId)).Approval!;

        // 3 September onwards: the second one only.
        PagedResult<ApprovalDto> recent = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { From = new DateOnly(2026, 9, 3) });

        Assert.Equal(later.Id, Assert.Single(recent.Items).Id);

        // The end of the range includes the whole of its day, rather than stopping at
        // midnight and dropping everything submitted during it.
        PagedResult<ApprovalDto> inclusive = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { To = new DateOnly(2026, 9, 1) });

        Assert.Single(inclusive.Items);
    }

    [SkippableFact]
    public async Task TheRegisterPages()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        for (int i = 0; i < 3; i++)
        {
            Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        PagedResult<ApprovalDto> first = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { Page = 1, PageSize = 2 });

        Assert.Equal(3, first.TotalItems);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasNextPage);

        PagedResult<ApprovalDto> second = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { Page = 2, PageSize = 2 });

        Assert.Single(second.Items);
        Assert.False(second.HasNextPage);
    }

    // ── what I submitted ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task MineListsOnlyWhatISubmitted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);
        ApprovalDto carasOwn = (await StartAsync(provider, _headUserId)).Approval!;

        PagedResult<ApprovalDto> mine =
            await As(provider, _headUserId).MineAsync(new PagedQuery());

        Assert.Equal(carasOwn.Id, Assert.Single(mine.Items).Id);
    }

    // The point of a submissions list as opposed to a queue: it does not stop mattering
    // once somebody has decided. "What happened to the leave I asked for" is the
    // question, and the answer is usually a decided approval.
    [SkippableFact]
    public async Task MineKeepsShowingApprovalsAlreadyDecided()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        // Ada is the line manager, so the one step is hers to decide.
        Assert.True((await As(provider, _managerUserId)
            .DecideAsync(raised.Id, ApprovalStepStatus.Approved)).Succeeded);

        PagedResult<ApprovalDto> mine =
            await As(provider, _headUserId).MineAsync(new PagedQuery());

        ApprovalDto only = Assert.Single(mine.Items);

        Assert.Equal(raised.Id, only.Id);
        Assert.Equal(ApprovalStatus.Approved, only.Status);
    }

    [SkippableFact]
    public async Task MineIsEmptyForSomebodyWhoHasRaisedNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        PagedResult<ApprovalDto> mine =
            await As(provider, _headUserId).MineAsync(new PagedQuery());

        Assert.Empty(mine.Items);
        Assert.Equal(0, mine.TotalItems);
    }

    [SkippableFact]
    public async Task MineIsNewestFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto older = (await StartAsync(provider, _headUserId)).Approval!;

        _clock.Advance(TimeSpan.FromHours(2));

        ApprovalDto newer = (await StartAsync(provider, _headUserId)).Approval!;

        PagedResult<ApprovalDto> mine =
            await As(provider, _headUserId).MineAsync(new PagedQuery());

        Assert.Equal(newer.Id, mine.Items[0].Id);
        Assert.Equal(older.Id, mine.Items[1].Id);
    }
}
