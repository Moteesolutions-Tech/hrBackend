using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// A role step is a queue, not a person, and that difference is the whole of phase 2.
// Everyone holding the level can see it; the first to act decides it; and who holds it is
// read at the moment somebody tries, never frozen at submission.
[Collection(PostgresCollection.Name)]
public class ApprovalRoleStepTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _roleId;
    private Guid _subjectId;
    private Guid _firstHolderUserId;
    private Guid _secondHolderUserId;
    private Guid _outsiderUserId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();
        _roleId = Guid.NewGuid();
        _subjectId = Guid.NewGuid();
        _firstHolderUserId = Guid.NewGuid();
        _secondHolderUserId = Guid.NewGuid();
        _outsiderUserId = Guid.NewGuid();

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
            seed.AccessLevels.Add(new AccessLevel
            {
                Id = _roleId,
                TenantId = _tenantId,
                Name = "HR Admin",
                Status = AccessLevelStatus.Active,
                Kind = AccessLevelKind.Custom,
                Scope = DataScope.Everything,
                Permissions = [],
            });

            Add(seed, _firstHolderUserId, "Ada", holdsRole: true);
            Add(seed, _secondHolderUserId, "Cara", holdsRole: true);
            Add(seed, _outsiderUserId, "Emeka", holdsRole: false);

            seed.Employees.Add(new Employee
            {
                Id = _subjectId,
                TenantId = _tenantId,
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
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

    private void Add(MoteeDbContext seed, Guid userId, string first, bool holdsRole)
    {
        Guid employeeId = Guid.NewGuid();

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

        if (holdsRole)
        {
            seed.UserAccessLevels.Add(new UserAccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                UserId = userId,
                AccessLevelId = _roleId,
                AssignedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private async Task<Guid> TemplateAsync(ServiceProvider provider, Guid? roleId = null)
    {
        ApprovalTemplateResult created = await Templates(provider).CreateAsync(
            new ApprovalTemplateRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                Name = "Leave approval",
                IsDefault = true,
                Steps =
                [
                    new ApprovalTemplateStepRequest
                    {
                        Label = "HR review",
                        Approver = ApproverResolver.Role,
                        RoleId = roleId ?? _roleId,
                        Required = true,
                    },
                ],
            });

        Assert.True(created.Succeeded, created.Outcome.ToString());

        return created.Template!.Id;
    }

    private static IApprovalTemplateService Templates(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IApprovalTemplateService>();

    private static IApprovalService As(ServiceProvider provider, Guid userId)
    {
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

        return provider.CreateScope().ServiceProvider.GetRequiredService<IApprovalService>();
    }

    private Task<ApprovalResult> StartAsync(ServiceProvider provider, Guid actingAs) =>
        As(provider, actingAs).StartAsync(new StartApprovalRequest
        {
            DocumentType = ApprovalDocumentTypes.LeaveRequest,
            SubjectType = "LeaveRequest",
            SubjectId = Guid.NewGuid(),
            SubjectEmployeeId = _subjectId,
        });

    // The step lands on the level rather than on a person, and says so in the name a
    // screen shows — "with HR Admin", the same field that says "with Ada Okafor".
    [SkippableFact]
    public async Task ARoleStepResolvesToTheLevelRatherThanAPerson()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        ApprovalResult started = await StartAsync(provider, _outsiderUserId);

        Assert.True(started.Succeeded, started.Outcome.ToString());

        ApprovalStepDto step = Assert.Single(started.Approval!.Steps);

        Assert.Equal(_roleId, step.ResolvedRoleId);
        Assert.Null(step.ResolvedEmployeeId);
        Assert.Equal("HR Admin", step.ResolvedName);
    }

    // The point of a queue: it appears for everybody who could clear it, not for one
    // arbitrarily chosen holder.
    [SkippableFact]
    public async Task EveryHolderSeesItInTheirQueue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);
        await StartAsync(provider, _outsiderUserId);

        Assert.Single((await As(provider, _firstHolderUserId).MyQueueAsync(new PagedQuery())).Items);
        Assert.Single((await As(provider, _secondHolderUserId).MyQueueAsync(new PagedQuery())).Items);

        // And not for somebody who does not hold it, which is the half that matters.
        Assert.Empty((await As(provider, _outsiderUserId).MyQueueAsync(new PagedQuery())).Items);
    }

    [SkippableFact]
    public async Task AnyHolderCanDecideItAndTheFirstToActDecides()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        ApprovalResult started = await StartAsync(provider, _outsiderUserId);
        Guid id = started.Approval!.Id;

        ApprovalResult decided = await As(provider, _secondHolderUserId)
            .DecideAsync(id, ApprovalStepStatus.Approved);

        Assert.True(decided.Succeeded, decided.Outcome.ToString());
        Assert.Equal(ApprovalStatus.Approved, decided.Approval!.Status);

        // It leaves the other holder's queue, because it is no longer waiting on anybody.
        Assert.Empty((await As(provider, _firstHolderUserId).MyQueueAsync(new PagedQuery())).Items);
    }

    [SkippableFact]
    public async Task SomebodyWithoutTheLevelCannotDecideIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        ApprovalResult started = await StartAsync(provider, _outsiderUserId);

        Assert.Equal(
            ApprovalOutcome.NotTheApprover,
            (await As(provider, _outsiderUserId)
                .DecideAsync(started.Approval!.Id, ApprovalStepStatus.Approved)).Outcome);
    }

    // The reason membership is read live rather than snapshotted. Somebody who joins the
    // team after a request was submitted must be able to clear it — otherwise a queue
    // becomes unreachable the moment the people in it change.
    [SkippableFact]
    public async Task SomebodyGivenTheLevelAfterSubmissionCanStillAct()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        ApprovalResult started = await StartAsync(provider, _outsiderUserId);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.UserAccessLevels.Add(new UserAccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                UserId = _outsiderUserId,
                AccessLevelId = _roleId,
                AssignedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        Assert.Single((await As(provider, _outsiderUserId).MyQueueAsync(new PagedQuery())).Items);

        Assert.True((await As(provider, _outsiderUserId)
            .DecideAsync(started.Approval!.Id, ApprovalStepStatus.Approved)).Succeeded);
    }

    // The other direction: losing the level takes the work away too, so a queue cannot be
    // cleared by somebody who has been moved off it.
    [SkippableFact]
    public async Task SomebodyWhoLosesTheLevelCanNoLongerAct()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        ApprovalResult started = await StartAsync(provider, _outsiderUserId);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.UserAccessLevels.RemoveRange(
                seed.UserAccessLevels.Where(assignment => assignment.UserId == _firstHolderUserId));

            await seed.SaveChangesAsync();
        }

        Assert.Empty((await As(provider, _firstHolderUserId).MyQueueAsync(new PagedQuery())).Items);

        Assert.Equal(
            ApprovalOutcome.NotTheApprover,
            (await As(provider, _firstHolderUserId)
                .DecideAsync(started.Approval!.Id, ApprovalStepStatus.Approved)).Outcome);
    }

    // A deactivated level grants nothing to anyone still holding it — that is what
    // deactivating means everywhere else, and a chain that ignored it would be a way
    // round it.
    [SkippableFact]
    public async Task ADeactivatedLevelResolvesToNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            AccessLevel level = await seed.AccessLevels.FirstAsync(item => item.Id == _roleId);
            level.Status = AccessLevelStatus.Inactive;

            await seed.SaveChangesAsync();
        }

        // The step was required, so the chain cannot start rather than silently
        // approving something nobody looked at.
        Assert.Equal(ApprovalOutcome.Unstartable, (await StartAsync(provider, _outsiderUserId)).Outcome);
    }

    // Approving your own request is not an approval, whether you were named by position
    // or reached through a role.
    [SkippableFact]
    public async Task TheOnlyHolderCannotApproveTheirOwnRequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider);

        Guid soleHolderEmployeeId;

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            // Leave one holder, and make the request about them.
            seed.UserAccessLevels.RemoveRange(
                seed.UserAccessLevels.Where(assignment => assignment.UserId == _secondHolderUserId));

            soleHolderEmployeeId = await seed.Users
                .Where(user => user.Id == _firstHolderUserId)
                .Select(user => user.EmployeeId!.Value)
                .FirstAsync();

            await seed.SaveChangesAsync();
        }

        ApprovalResult started = await As(provider, _firstHolderUserId).StartAsync(
            new StartApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                SubjectType = "LeaveRequest",
                SubjectId = Guid.NewGuid(),
                SubjectEmployeeId = soleHolderEmployeeId,
            });

        Assert.Equal(ApprovalOutcome.Unstartable, started.Outcome);
    }

    // A level with nobody in it is a queue nobody will ever open. Saying so at submission
    // is the difference between a chain that stalls visibly and one that stalls silently.
    [SkippableFact]
    public async Task AnEmptyLevelResolvesToNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid emptyRoleId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.AccessLevels.Add(new AccessLevel
            {
                Id = emptyRoleId,
                TenantId = _tenantId,
                Name = "Finance",
                Status = AccessLevelStatus.Active,
                Kind = AccessLevelKind.Custom,
                Scope = DataScope.Everything,
                Permissions = [],
            });

            await seed.SaveChangesAsync();
        }

        await TemplateAsync(provider, emptyRoleId);

        Assert.Equal(ApprovalOutcome.Unstartable, (await StartAsync(provider, _outsiderUserId)).Outcome);
    }

    // Caught at save rather than left to fail at resolution, where it would present as an
    // approval mysteriously skipping a step somebody configured on purpose.
    [SkippableFact]
    public async Task ARoleStepNamingNoLevelIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalTemplateResult created = await Templates(provider).CreateAsync(
            new ApprovalTemplateRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                Name = "Broken",
                Steps =
                [
                    new ApprovalTemplateStepRequest
                    {
                        Label = "HR review",
                        Approver = ApproverResolver.Role,
                        Required = true,
                    },
                ],
            });

        Assert.Equal(ApprovalTemplateOutcome.RoleMissing, created.Outcome);
    }

    // Another company's access level is not a level this tenant can name. The tenant
    // filter answers this on its own — it is simply not found — but a chain that could
    // point at one would put another company's staff in this company's queue.
    [SkippableFact]
    public async Task ARoleStepCannotNameAnotherCompanysLevel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid otherTenantId = Guid.NewGuid();
        Guid theirRoleId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = otherTenantId,
                Name = "Other",
                Slug = $"other-{otherTenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            seed.AccessLevels.Add(new AccessLevel
            {
                Id = theirRoleId,
                TenantId = otherTenantId,
                Name = "Their HR",
                Status = AccessLevelStatus.Active,
                Kind = AccessLevelKind.Custom,
                Scope = DataScope.Everything,
                Permissions = [],
            });

            await seed.SaveChangesAsync();
        }

        ApprovalTemplateResult created = await Templates(provider).CreateAsync(
            new ApprovalTemplateRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                Name = "Cross tenant",
                Steps =
                [
                    new ApprovalTemplateStepRequest
                    {
                        Label = "HR review",
                        Approver = ApproverResolver.Role,
                        RoleId = theirRoleId,
                        Required = true,
                    },
                ],
            });

        Assert.Equal(ApprovalTemplateOutcome.RoleMissing, created.Outcome);
    }

    // A role id left behind on a step later switched to a positional rule would sit there
    // looking authoritative and be read by nothing.
    [SkippableFact]
    public async Task SwitchingAStepAwayFromRoleDropsTheRole()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid templateId = await TemplateAsync(provider);

        ApprovalTemplateResult updated = await Templates(provider).UpdateAsync(
            templateId,
            new ApprovalTemplateRequest
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
                        RoleId = _roleId,
                        Required = true,
                    },
                ],
            });

        Assert.True(updated.Succeeded, updated.Outcome.ToString());

        ApprovalTemplateStepDto step = Assert.Single(updated.Template!.Steps);

        Assert.Null(step.RoleId);
        Assert.Null(step.RoleName);
    }

    // The recovery path for a chain stopped against a gap in the org chart.
    //
    // Steps resolve once, at submission, so fixing the cause afterwards does not unstick
    // anything on its own — and the stuck step has no approver, so no decision path ever
    // runs against it again. Without this the only way out is cancelling and losing the
    // approvals already given.
    [SkippableFact]
    public async Task ABlockedChainCanBeAskedToLookAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid emptyRoleId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.AccessLevels.Add(new AccessLevel
            {
                Id = emptyRoleId,
                TenantId = _tenantId,
                Name = "Finance",
                Status = AccessLevelStatus.Active,
                Kind = AccessLevelKind.Custom,
                Scope = DataScope.Everything,
                Permissions = [],
            });

            await seed.SaveChangesAsync();
        }

        // Two steps: one that resolves, then one that does not. The chain starts, because
        // there is work to be getting on with, and stops at the empty level.
        ApprovalTemplateResult template = await Templates(provider).CreateAsync(
            new ApprovalTemplateRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                Name = "Leave approval",
                IsDefault = true,
                Steps =
                [
                    new ApprovalTemplateStepRequest
                    {
                        Label = "HR review",
                        Approver = ApproverResolver.Role,
                        RoleId = _roleId,
                        Required = true,
                    },
                    new ApprovalTemplateStepRequest
                    {
                        Label = "Finance review",
                        Approver = ApproverResolver.Role,
                        RoleId = emptyRoleId,
                        Required = true,
                    },
                ],
            });

        Assert.True(template.Succeeded, template.Outcome.ToString());

        ApprovalResult started = await StartAsync(provider, _outsiderUserId);
        Assert.True(started.Succeeded, started.Outcome.ToString());

        Guid id = started.Approval!.Id;

        ApprovalResult afterFirst = await As(provider, _firstHolderUserId)
            .DecideAsync(id, ApprovalStepStatus.Approved);

        // Stopped, and saying so rather than looking like ordinary progress — otherwise
        // it ages quietly in nobody's queue.
        Assert.Equal(ApprovalStatus.InProgress, afterFirst.Approval!.Status);
        Assert.True(afterFirst.Approval.IsBlocked);
        Assert.Contains("Finance", afterFirst.Approval.BlockedReason!, StringComparison.Ordinal);

        // Somebody joins Finance.
        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.UserAccessLevels.Add(new UserAccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                UserId = _secondHolderUserId,
                AccessLevelId = emptyRoleId,
                AssignedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ApprovalResult looked = await As(provider, _outsiderUserId).ReresolveAsync(id);

        Assert.True(looked.Succeeded, looked.Outcome.ToString());
        Assert.False(looked.Approval!.IsBlocked);

        // And it is now genuinely actionable, not merely un-flagged.
        Assert.Single((await As(provider, _secondHolderUserId).MyQueueAsync(new PagedQuery())).Items);

        Assert.True((await As(provider, _secondHolderUserId)
            .DecideAsync(id, ApprovalStepStatus.Approved)).Succeeded);
    }

    // The common case, handled without anybody having to ask: the gap is filled while an
    // earlier step is still being decided, and the chain picks it up as it moves.
    [SkippableFact]
    public async Task AGapFilledWhileAnEarlierStepIsDecidedIsPickedUpAutomatically()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid emptyRoleId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.AccessLevels.Add(new AccessLevel
            {
                Id = emptyRoleId,
                TenantId = _tenantId,
                Name = "Finance",
                Status = AccessLevelStatus.Active,
                Kind = AccessLevelKind.Custom,
                Scope = DataScope.Everything,
                Permissions = [],
            });

            await seed.SaveChangesAsync();
        }

        await Templates(provider).CreateAsync(new ApprovalTemplateRequest
        {
            DocumentType = ApprovalDocumentTypes.LeaveRequest,
            Name = "Leave approval",
            IsDefault = true,
            Steps =
            [
                new ApprovalTemplateStepRequest
                {
                    Label = "HR review",
                    Approver = ApproverResolver.Role,
                    RoleId = _roleId,
                    Required = true,
                },
                new ApprovalTemplateStepRequest
                {
                    Label = "Finance review",
                    Approver = ApproverResolver.Role,
                    RoleId = emptyRoleId,
                    Required = true,
                },
            ],
        });

        Guid id = (await StartAsync(provider, _outsiderUserId)).Approval!.Id;

        // Filled before the first approver gets round to deciding.
        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.UserAccessLevels.Add(new UserAccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                UserId = _secondHolderUserId,
                AccessLevelId = emptyRoleId,
                AssignedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ApprovalResult afterFirst = await As(provider, _firstHolderUserId)
            .DecideAsync(id, ApprovalStepStatus.Approved);

        Assert.False(afterFirst.Approval!.IsBlocked);
        Assert.Equal(emptyRoleId, afterFirst.Approval.CurrentStep!.ResolvedRoleId);
    }

    // Picking "Role" is only half a choice, so the catalogue carries the levels to
    // complete it — with the holder count, because a level with nobody in it is worth
    // seeing before it is chosen rather than after a chain stalls on it.
    [SkippableFact]
    public async Task TheCatalogueOffersTheLevelsAndSaysHowManyHoldThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalRoleDto role = Assert.Single(await Templates(provider).RolesAsync());

        Assert.Equal(_roleId, role.Id);
        Assert.Equal("HR Admin", role.Name);
        Assert.Equal(2, role.Holders);
    }
}
