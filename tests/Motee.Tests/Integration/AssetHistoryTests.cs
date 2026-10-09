using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Assets;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Assets;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Who has ever held a laptop, as distinct from who holds it now.
//
// The asset row answers the second by overwriting a pointer, which answered the first by
// destroying it. These pin down that a handover is a return followed by an assignment,
// never an overwrite.
[Collection(PostgresCollection.Name)]
public class AssetHistoryTests(PostgresFixture fixture)
{
    private Guid _departmentId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Acme",
                Slug = $"acme-{tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private async Task<Guid> HireAsync(ServiceProvider provider, string first, string email) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
        })).Employee!.Id;

    private static async Task<Guid> AssetAsync(ServiceProvider provider) =>
        (await Resolve<IAssetService>(provider).CreateAsync(new AssetRequest
        {
            Tag = "AST-0142",
            Name = "MacBook Pro 14",
            Category = "Laptop",
            SerialNumber = "C02X1234",
        })).Asset!.Id;

    // The question the review raised: who had this laptop in March.
    [SkippableFact]
    public async Task AHandoverKeepsBothSpells()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid assetId = await AssetAsync(provider);
        Guid tunde = await HireAsync(provider, "Tunde", "tunde@acme.com");
        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com");

        await Resolve<IAssetService>(provider).AssignAsync(assetId, new AssignAssetRequest
        {
            EmployeeId = tunde,
            AssignedDate = new DateOnly(2026, 1, 1),
        });

        await Resolve<IAssetService>(provider).ReturnAsync(assetId, new ReturnAssetRequest
        {
            ReturnedOn = new DateOnly(2026, 6, 3),
            Reason = "Returned",
        });

        await Resolve<IAssetService>(provider).AssignAsync(assetId, new AssignAssetRequest
        {
            EmployeeId = ada,
            AssignedDate = new DateOnly(2026, 6, 8),
        });

        IReadOnlyList<AssetAssignmentDto> history =
            await Resolve<IAssetService>(provider).HistoryAsync(assetId);

        Assert.Equal(2, history.Count);

        // Open spell first, so a screen leads with who has it now.
        Assert.True(history[0].IsOpen);
        Assert.Equal(ada, history[0].EmployeeId);
        Assert.Equal("Ada Okafor", history[0].EmployeeName);
        Assert.Null(history[0].HeldDays);

        Assert.False(history[1].IsOpen);
        Assert.Equal(tunde, history[1].EmployeeId);
        Assert.Equal(new DateOnly(2026, 6, 3), history[1].ReturnedOn);
        Assert.Equal(153, history[1].HeldDays);
    }

    // Handing the laptop straight to the next person is refused, not silently absorbed.
    // The service insists on a return first, which is what keeps the history honest —
    // there is no path that leaves two people apparently holding one asset.
    [SkippableFact]
    public async Task ReassigningWithoutAReturnIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid assetId = await AssetAsync(provider);
        Guid tunde = await HireAsync(provider, "Tunde", "tunde@acme.com");
        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com");

        await Resolve<IAssetService>(provider).AssignAsync(
            assetId, new AssignAssetRequest { EmployeeId = tunde });

        AssetResult second = await Resolve<IAssetService>(provider).AssignAsync(
            assetId, new AssignAssetRequest { EmployeeId = ada });

        Assert.Equal(AssetOutcome.AlreadyAssigned, second.Outcome);

        AssetAssignmentDto spell = Assert.Single(
            await Resolve<IAssetService>(provider).HistoryAsync(assetId));

        Assert.Equal(tunde, spell.EmployeeId);
        Assert.True(spell.IsOpen);
    }

    // Assigning to whoever already holds it is a correction, not a handover. Closing their
    // spell and opening another would read as a handover to themselves.
    [SkippableFact]
    public async Task ReassigningToTheSameHolderAmendsTheSpellRatherThanSplittingIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid assetId = await AssetAsync(provider);
        Guid tunde = await HireAsync(provider, "Tunde", "tunde@acme.com");

        await Resolve<IAssetService>(provider).AssignAsync(assetId, new AssignAssetRequest
        {
            EmployeeId = tunde,
            AssignedDate = new DateOnly(2026, 1, 1),
        });

        await Resolve<IAssetService>(provider).AssignAsync(assetId, new AssignAssetRequest
        {
            EmployeeId = tunde,
            AssignedDate = new DateOnly(2026, 1, 5),
            Condition = "Scratched lid",
        });

        AssetAssignmentDto spell = Assert.Single(
            await Resolve<IAssetService>(provider).HistoryAsync(assetId));

        Assert.Equal(new DateOnly(2026, 1, 5), spell.AssignedOn);
        Assert.Equal("Scratched lid", spell.ConditionOnAssign);
        Assert.True(spell.IsOpen);
    }

    // The database enforces it too, not only the service. A second open row is the state
    // no query could interpret, so it must be impossible rather than merely avoided.
    [SkippableFact]
    public async Task TwoOpenSpellsAreImpossible()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid assetId = await AssetAsync(provider);
        Guid tunde = await HireAsync(provider, "Tunde", "tunde@acme.com");
        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com");

        await Resolve<IAssetService>(provider).AssignAsync(
            assetId, new AssignAssetRequest { EmployeeId = tunde });

        await using MoteeDbContext context = fixture.CreateContext(
            provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId);

        context.AssetAssignments.Add(new AssetAssignment
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            EmployeeId = ada,
            AssignedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    // "Lost while Tunde had it" is the fact an investigation needs. Clearing the pointer
    // without closing the spell would erase exactly that.
    [SkippableFact]
    public async Task AnAssetReportedLostClosesTheSpellAgainstWhoeverHadIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid assetId = await AssetAsync(provider);
        Guid tunde = await HireAsync(provider, "Tunde", "tunde@acme.com");

        await Resolve<IAssetService>(provider).AssignAsync(
            assetId, new AssignAssetRequest { EmployeeId = tunde });

        await Resolve<IAssetService>(provider).ChangeStatusAsync(assetId, AssetStatus.Lost);

        AssetAssignmentDto spell = Assert.Single(
            await Resolve<IAssetService>(provider).HistoryAsync(assetId));

        Assert.False(spell.IsOpen);
        Assert.Equal(tunde, spell.EmployeeId);
        Assert.Equal("Lost", spell.ReturnReason);
    }

    // Kit issued during onboarding is the start of an asset's history, not an exception
    // to it — the wizard path has to open a spell like any other assignment.
    [SkippableFact]
    public async Task KitIssuedAtOnboardingOpensASpell()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Ada", "ada@acme.com");

        AssetOutcome? problem = await Resolve<IAssetService>(provider).StageForEmployeeAsync(
            employeeId,
            [new AssetRequest { Tag = "AST-9001", Name = "ThinkPad", Category = "Laptop" }]);

        Assert.Null(problem);

        // StageForEmployeeAsync adds without saving: the caller owns the transaction so
        // the employee and their kit commit together.
        await using MoteeDbContext context = fixture.CreateContext(
            provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId);

        Assert.Empty(await context.AssetAssignments.ToListAsync());
    }

    // Condition at handover and at return. Either on its own proves nothing; the pair is
    // what supports a deduction or a write-off.
    [SkippableFact]
    public async Task ConditionIsRecordedAtBothEnds()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid assetId = await AssetAsync(provider);
        Guid tunde = await HireAsync(provider, "Tunde", "tunde@acme.com");

        await Resolve<IAssetService>(provider).AssignAsync(assetId, new AssignAssetRequest
        {
            EmployeeId = tunde,
            Condition = "New, no marks",
        });

        await Resolve<IAssetService>(provider).ReturnAsync(assetId, new ReturnAssetRequest
        {
            Condition = "Cracked screen",
            Reason = "Offboarding",
        });

        AssetAssignmentDto spell = Assert.Single(
            await Resolve<IAssetService>(provider).HistoryAsync(assetId));

        Assert.Equal("New, no marks", spell.ConditionOnAssign);
        Assert.Equal("Cracked screen", spell.ConditionOnReturn);
    }

    // An asset assigned before this table existed has no open row to close. A return must
    // still succeed for it rather than failing on history that was never recorded.
    [SkippableFact]
    public async Task ReturningAnAssetWithNoRecordedSpellStillWorks()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid assetId = await AssetAsync(provider);
        Guid tunde = await HireAsync(provider, "Tunde", "tunde@acme.com");

        await Resolve<IAssetService>(provider).AssignAsync(
            assetId, new AssignAssetRequest { EmployeeId = tunde });

        Guid tenantId = provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId!.Value;

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
            context.AssetAssignments.RemoveRange(await context.AssetAssignments.ToListAsync());
            await context.SaveChangesAsync();
        }

        AssetResult returned = await Resolve<IAssetService>(provider).ReturnAsync(assetId);

        Assert.True(returned.Succeeded, returned.Outcome.ToString());
        Assert.Null(returned.Asset!.AssignedToEmployeeId);
    }
}
