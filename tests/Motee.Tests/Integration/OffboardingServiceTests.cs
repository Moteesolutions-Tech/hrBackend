using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Offboarding;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Offboarding;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The lifecycle rules are tested without a database in OffboardingLifecycleTests. These
// cover what a transition *means*: an exit record that says someone left while the
// employee row still says Active is worse than no record at all.
[Collection(PostgresCollection.Name)]
public class OffboardingServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;

    private async Task<ServiceProvider> ArrangeAsync(
        EmployeeStatus status = EmployeeStatus.Active)
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();
        _employeeId = Guid.NewGuid();

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
            seed.Employees.Add(new Employee
            {
                Id = _employeeId,
                TenantId = _tenantId,
                FirstName = "Ada",
                LastName = "Okafor",
                Email = "ada@acme.com",
                JobTitle = "Analyst",
                Status = status,
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

    private static IOffboardingService Service(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IOffboardingService>();

    private async Task<EmployeeStatus> EmployeeStatusAsync()
    {
        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        return (await context.Employees.FirstAsync(employee => employee.Id == _employeeId)).Status;
    }

    private Task<OffboardingResult> InitiateAsync(IOffboardingService service) =>
        service.InitiateAsync(new InitiateOffboardingRequest
        {
            EmployeeId = _employeeId,
            ExitReason = ExitReason.Resignation,
            LastWorkingDate = new DateOnly(2026, 9, 30),
            Notes = "Moving abroad",
        });

    [SkippableFact]
    public async Task InitiatingCreatesTheStandardChecklistAndMarksThemLeaving()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        OffboardingResult result = await InitiateAsync(Service(provider));

        Assert.True(result.Succeeded);
        Assert.Equal(OffboardingStatus.Pending, result.Record!.Status);

        // Seven items, in the order they should be worked.
        Assert.Equal(7, result.Record.Clearance.Count);
        Assert.Equal("Acknowledge resignation", result.Record.Clearance[0].Label);
        Assert.All(result.Record.Clearance, item => Assert.False(item.Completed));

        // Notice served is a change to their employment, visible on the employee record
        // rather than only inside this module.
        Assert.Equal(EmployeeStatus.Offboarding, await EmployeeStatusAsync());
    }

    // The response carries what may happen next, so the client never keeps its own copy
    // of the rules and cannot enable a button the backend then refuses.
    [SkippableFact]
    public async Task TheRecordSaysWhichActionsAreAvailable()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        OffboardingResult result = await InitiateAsync(Service(provider));

        Assert.Contains(OffboardingAction.Approve, result.Record!.AvailableActions);
        Assert.Contains(OffboardingAction.Disapprove, result.Record.AvailableActions);
        Assert.DoesNotContain(OffboardingAction.Complete, result.Record.AvailableActions);
    }

    // One person, one live exit. Two would mean two exit dates and two checklists, and
    // payroll would have to guess which to pay against.
    [SkippableFact]
    public async Task ASecondLiveExitIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await InitiateAsync(Service(provider));

        OffboardingResult second = await InitiateAsync(Service(provider));

        Assert.Equal(OffboardingOutcome.AlreadyOffboarding, second.Outcome);
    }

    [SkippableFact]
    public async Task TurningDownAnExitNeedsAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await InitiateAsync(Service(provider))).Record!.Id;

        OffboardingResult refused = await Service(provider)
            .ApplyAsync(id, OffboardingAction.Disapprove);

        Assert.Equal(OffboardingOutcome.ReasonRequired, refused.Outcome);

        OffboardingResult withReason = await Service(provider)
            .ApplyAsync(id, OffboardingAction.Disapprove, "Retention offer accepted");

        Assert.True(withReason.Succeeded);
        Assert.Equal("Retention offer accepted", withReason.Record!.DecisionReason);
    }

    // Ticking the first item is what moves an approved exit into clearance. Otherwise
    // InProgress is a state nothing ever enters.
    [SkippableFact]
    public async Task TheFirstClearanceTickStartsTheClearance()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await InitiateAsync(Service(provider))).Record!.Id;

        OffboardingResult approved = await Service(provider)
            .ApplyAsync(id, OffboardingAction.Approve);

        Assert.Equal(OffboardingStatus.Approved, approved.Record!.Status);

        OffboardingResult ticked = await Service(provider).CompleteClearanceAsync(
            id, approved.Record.Clearance[0].Id, "Acknowledged by email");

        Assert.Equal(OffboardingStatus.InProgress, ticked.Record!.Status);
        Assert.True(ticked.Record.Clearance[0].Completed);
        Assert.NotNull(ticked.Record.Clearance[0].CompletedAt);
        Assert.NotNull(ticked.Record.Clearance[0].CompletedByUserId);
    }

    // The point of the whole record: they have gone, and every report reads that from
    // the employee row rather than from this module.
    [SkippableFact]
    public async Task CompletingMarksThemInactiveWithTheirLeavingDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await InitiateAsync(Service(provider))).Record!.Id;

        await Service(provider).ApplyAsync(id, OffboardingAction.Approve);
        await Service(provider).ApplyAsync(id, OffboardingAction.Complete);

        Assert.Equal(EmployeeStatus.Inactive, await EmployeeStatusAsync());

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);
        Employee employee = await context.Employees.FirstAsync(candidate => candidate.Id == _employeeId);

        Assert.Equal(new DateOnly(2026, 9, 30), employee.DateOfLeaving);
    }

    // Notice withdrawn: they are staying, and the employee row has to say so.
    [SkippableFact]
    public async Task ReactivatingPutsThemBackToActive()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await InitiateAsync(Service(provider))).Record!.Id;

        await Service(provider).ApplyAsync(id, OffboardingAction.Approve);

        OffboardingResult reactivated = await Service(provider)
            .ApplyAsync(id, OffboardingAction.Reactivate);

        Assert.Equal(OffboardingStatus.Reactivated, reactivated.Record!.Status);
        Assert.NotNull(reactivated.Record.ReactivatedAt);
        Assert.Equal(EmployeeStatus.Active, await EmployeeStatusAsync());
    }

    // Once they have gone, no button undoes it — and because reactivating is refused,
    // the employee row can never disagree with the exit record.
    [SkippableFact]
    public async Task ADepartureCannotBeUndone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await InitiateAsync(Service(provider))).Record!.Id;

        await Service(provider).ApplyAsync(id, OffboardingAction.Approve);
        await Service(provider).ApplyAsync(id, OffboardingAction.Complete);

        OffboardingResult undo = await Service(provider)
            .ApplyAsync(id, OffboardingAction.Reactivate);

        Assert.Equal(OffboardingOutcome.NotAllowed, undo.Outcome);
        Assert.Equal(EmployeeStatus.Inactive, await EmployeeStatusAsync());
    }

    // Cutting access while the resignation is still being decided is a decision taken
    // by accident.
    [SkippableFact]
    public async Task AccessCannotBeRevokedBeforeTheExitIsAgreed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await InitiateAsync(Service(provider))).Record!.Id;

        Assert.Equal(
            OffboardingOutcome.NotAllowed,
            (await Service(provider).RevokeAccessAsync(id)).Outcome);

        await Service(provider).ApplyAsync(id, OffboardingAction.Approve);

        OffboardingResult revoked = await Service(provider).RevokeAccessAsync(id);

        Assert.True(revoked.Succeeded);
        Assert.NotNull(revoked.Record!.SystemAccessRevokedAt);
    }

    // A completed exit is the record of somebody leaving. Deleting it would erase why
    // they went and what was returned, which is the one thing this table exists for.
    [SkippableFact]
    public async Task ACompletedExitCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await InitiateAsync(Service(provider))).Record!.Id;

        await Service(provider).ApplyAsync(id, OffboardingAction.Approve);
        await Service(provider).ApplyAsync(id, OffboardingAction.Complete);

        Assert.Equal(OffboardingOutcome.NotAllowed, await Service(provider).DeleteAsync(id));
    }

    // Editing the exit date of a departure that already happened rewrites what payroll
    // acted on.
    [SkippableFact]
    public async Task AFinishedRecordCannotBeEdited()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await InitiateAsync(Service(provider))).Record!.Id;

        await Service(provider).ApplyAsync(id, OffboardingAction.Approve);
        await Service(provider).ApplyAsync(id, OffboardingAction.Complete);

        OffboardingResult edited = await Service(provider).UpdateAsync(id, new UpdateOffboardingRequest
        {
            ExitReason = ExitReason.Redundancy,
            LastWorkingDate = new DateOnly(2026, 12, 31),
        });

        Assert.Equal(OffboardingOutcome.NotAllowed, edited.Outcome);
    }

    [SkippableFact]
    public async Task ClearanceOnARefusedExitIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        OffboardingDto record = (await InitiateAsync(Service(provider))).Record!;

        await Service(provider).ApplyAsync(
            record.Id, OffboardingAction.Disapprove, "Retention offer accepted");

        OffboardingResult ticked = await Service(provider)
            .CompleteClearanceAsync(record.Id, record.Clearance[0].Id);

        Assert.Equal(OffboardingOutcome.NotAllowed, ticked.Outcome);
    }

    [SkippableFact]
    public async Task StatsCountWhatHrChases()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await InitiateAsync(Service(provider));

        OffboardingStatsDto stats = await Service(provider).StatsAsync();

        Assert.Equal(1, stats.Total);
        Assert.Equal(1, stats.Pending);

        // Still leaving, and seven items outstanding.
        Assert.Equal(1, stats.ClearancePending);
        Assert.Equal(0, stats.Completed);
    }
}
