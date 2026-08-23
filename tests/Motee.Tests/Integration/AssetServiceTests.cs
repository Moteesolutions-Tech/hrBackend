using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Assets;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Assets;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class AssetServiceTests(PostgresFixture fixture)
{
    private async Task<(Guid TenantId, Guid DepartmentId, ServiceProvider Provider)> ArrangeAsync()
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

        Guid departmentId = (await provider.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return (tenantId, departmentId, provider);
    }

    private static IAssetService Service(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IAssetService>();

    private static IEmployeeService Employees(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IEmployeeService>();

    private static AssetRequest Request(
        string tag = "AST-0142",
        string name = "MacBook Pro 14",
        string serial = "C02X1234") =>
        new() { Tag = tag, Name = name, Category = "Laptop", SerialNumber = serial };

    private static AssetQuery AllScope() => new() { Scope = DataScope.Everything };

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        Guid departmentId,
        string email = "ada@acme.com") =>
        (await Employees(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
        })).Employee!.Id;

    [SkippableFact]
    public async Task CreatesAnAssetAsAvailable()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        AssetResult result = await Service(owned).CreateAsync(Request());

        Assert.True(result.Succeeded, result.Outcome.ToString());
        Assert.Equal(AssetStatus.Available, result.Asset!.Status);
        Assert.Null(result.Asset.AssignedToEmployeeId);
    }

    // The tag is what someone reads off the sticker, so two assets cannot share one.
    [SkippableFact]
    public async Task RefusesADuplicateTag()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request());

        Assert.Equal(
            AssetOutcome.DuplicateTag,
            (await Service(owned).CreateAsync(Request(tag: "ast-0142"))).Outcome);
    }

    [SkippableFact]
    public async Task AssigningRecordsTheHolderAndTheDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid assetId = (await Service(owned).CreateAsync(Request())).Asset!.Id;
        Guid employeeId = await HireAsync(owned, departmentId);

        AssetResult result = await Service(owned).AssignAsync(assetId, new AssignAssetRequest
        {
            EmployeeId = employeeId,
            AssignedDate = new DateOnly(2026, 5, 1),
        });

        Assert.Equal(AssetStatus.Assigned, result.Asset!.Status);
        Assert.Equal(employeeId, result.Asset.AssignedToEmployeeId);
        Assert.Equal("Ada Okafor", result.Asset.AssignedToName);
        Assert.Equal(new DateOnly(2026, 5, 1), result.Asset.AssignedDate);
    }

    // Two people cannot both be holding the same machine.
    [SkippableFact]
    public async Task RefusesToAssignSomethingSomeoneElseHolds()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid assetId = (await Service(owned).CreateAsync(Request())).Asset!.Id;
        Guid first = await HireAsync(owned, departmentId);
        Guid second = await HireAsync(owned, departmentId, "bola@acme.com");

        await Service(owned).AssignAsync(assetId, new AssignAssetRequest { EmployeeId = first });

        Assert.Equal(
            AssetOutcome.AlreadyAssigned,
            (await Service(owned).AssignAsync(assetId, new AssignAssetRequest { EmployeeId = second }))
                .Outcome);
    }

    [SkippableFact]
    public async Task ReturningPutsItBackOnTheShelf()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid assetId = (await Service(owned).CreateAsync(Request())).Asset!.Id;
        Guid employeeId = await HireAsync(owned, departmentId);

        await Service(owned).AssignAsync(assetId, new AssignAssetRequest { EmployeeId = employeeId });

        AssetResult returned = await Service(owned).ReturnAsync(assetId);

        Assert.Equal(AssetStatus.Available, returned.Asset!.Status);
        Assert.Null(returned.Asset.AssignedToEmployeeId);
        Assert.Null(returned.Asset.AssignedDate);
    }

    [SkippableFact]
    public async Task AssigningToSomeoneWhoDoesNotExistIsRejected()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid assetId = (await Service(owned).CreateAsync(Request())).Asset!.Id;

        Assert.Equal(
            AssetOutcome.UnknownEmployee,
            (await Service(owned).AssignAsync(
                assetId, new AssignAssetRequest { EmployeeId = Guid.NewGuid() })).Outcome);
    }

    // Scrapped is scrapped. Bringing one back is a new record.
    [SkippableFact]
    public async Task ARetiredAssetCannotBeAssignedOrRevived()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid assetId = (await Service(owned).CreateAsync(Request())).Asset!.Id;
        Guid employeeId = await HireAsync(owned, departmentId);

        await Service(owned).ChangeStatusAsync(assetId, AssetStatus.Retired);

        Assert.Equal(
            AssetOutcome.InvalidStatusChange,
            (await Service(owned).AssignAsync(
                assetId, new AssignAssetRequest { EmployeeId = employeeId })).Outcome);

        Assert.Equal(
            AssetOutcome.InvalidStatusChange,
            (await Service(owned).ChangeStatusAsync(assetId, AssetStatus.Available)).Outcome);
    }

    // A laptop that goes missing while someone holds it is nobody's any more.
    [SkippableFact]
    public async Task MarkingSomethingLostClearsTheHolder()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid assetId = (await Service(owned).CreateAsync(Request())).Asset!.Id;
        Guid employeeId = await HireAsync(owned, departmentId);

        await Service(owned).AssignAsync(assetId, new AssignAssetRequest { EmployeeId = employeeId });

        AssetResult lost = await Service(owned).ChangeStatusAsync(assetId, AssetStatus.Lost);

        Assert.Equal(AssetStatus.Lost, lost.Asset!.Status);
        Assert.Null(lost.Asset.AssignedToEmployeeId);
    }

    [SkippableFact]
    public async Task SearchesTagNameAndSerial()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request());
        await Service(owned).CreateAsync(
            Request(tag: "AST-0200", name: "iPhone 15", serial: "F17Y9999"));

        Assert.Single((await Service(owned).ListAsync(AllScope() with { Search = "0142" })).Items);
        Assert.Single((await Service(owned).ListAsync(AllScope() with { Search = "iphone" })).Items);
        Assert.Single((await Service(owned).ListAsync(AllScope() with { Search = "f17y" })).Items);
    }

    // Self-service grants View on assets, and it must mean "mine", not "everyone's".
    [SkippableFact]
    public async Task SelfScopeSeesOnlyWhatTheViewerHolds()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid mine = (await Service(owned).CreateAsync(Request())).Asset!.Id;
        await Service(owned).CreateAsync(Request(tag: "AST-0200", name: "iPhone 15"));

        Guid viewer = await HireAsync(owned, departmentId);
        await Service(owned).AssignAsync(mine, new AssignAssetRequest { EmployeeId = viewer });

        PagedResult<AssetDto> visible = await Service(owned).ListAsync(new AssetQuery
        {
            Scope = new DataScope { Kind = DataScopeKind.Self },
            ViewerEmployeeId = viewer,
        });

        Assert.Equal("AST-0142", visible.Items.Single().Tag);
    }

    [SkippableFact]
    public async Task NoScopeSeesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request());

        Assert.Empty((await Service(owned)
            .ListAsync(new AssetQuery { Scope = DataScope.Nothing })).Items);
    }

    [SkippableFact]
    public async Task AnAssetIsInvisibleToAnotherTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid assetId = (await Service(owned).CreateAsync(Request())).Asset!.Id;

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = Guid.NewGuid();

        Assert.Null(await Service(owned).GetAsync(assetId));
        Assert.Empty((await Service(owned).ListAsync(AllScope())).Items);
    }

    // Two companies may both label something "AST-0001".
    [SkippableFact]
    public async Task TheSameTagIsFreeInAnotherTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request());

        Guid other = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = other,
                Name = "Globex",
                Slug = $"globex-{other:N}",
                CountryCode = CountryCode.UnitedKingdom,
            });

            await seed.SaveChangesAsync();
        }

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other;

        Assert.True((await Service(owned).CreateAsync(Request())).Succeeded);
    }

    // ---- the wizard's Assets step ----

    [SkippableFact]
    public async Task TheWizardCreatesAndAssignsKitWithTheEmployee()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeResult hired = await Employees(owned).CreateAsync(new EmployeeRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
            Assets = [Request(), Request(tag: "AST-0200", name: "iPhone 15")],
        });

        Assert.True(hired.Succeeded, hired.Outcome.ToString());

        PagedResult<AssetDto> theirs = await Service(owned)
            .ListAsync(AllScope() with { AssignedToEmployeeId = hired.Employee!.Id });

        Assert.Equal(2, theirs.TotalItems);
        Assert.All(theirs.Items, asset => Assert.Equal(AssetStatus.Assigned, asset.Status));
    }

    // The whole form commits together, so a clashing tag must not leave a
    // half-onboarded person behind.
    [SkippableFact]
    public async Task AClashingTagInTheWizardRollsBackTheWholeHire()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request());

        EmployeeResult hired = await Employees(owned).CreateAsync(new EmployeeRequest
        {
            FirstName = "Bola",
            LastName = "Okafor",
            Email = "bola@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
            Assets = [Request()],
        });

        Assert.Equal(EmployeeOutcome.DuplicateAssetTag, hired.Outcome);

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Assert.False(await context.Employees.AnyAsync(employee => employee.Email == "bola@acme.com"));
    }

    // Both rows pass the database check, which only sees what is committed.
    [SkippableFact]
    public async Task TwoRowsInOneSubmissionCannotShareATag()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeResult hired = await Employees(owned).CreateAsync(new EmployeeRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
            Assets = [Request(), Request(name: "Another laptop")],
        });

        Assert.Equal(EmployeeOutcome.DuplicateAssetTag, hired.Outcome);
    }

    // Deleting a person must not delete company property, and must not quietly leave
    // a laptop marked Assigned with nobody holding it. Erasing them is refused until
    // the kit comes back. The app soft-deletes employees, so this only bites on a
    // hard delete — where being stopped is the point.
    [SkippableFact]
    public async Task SomeoneStillHoldingKitCannotBeErased()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid assetId = (await Service(owned).CreateAsync(Request())).Asset!.Id;
        Guid employeeId = await HireAsync(owned, departmentId);

        await Service(owned).AssignAsync(assetId, new AssignAssetRequest { EmployeeId = employeeId });

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
            context.Employees.Remove(
                await context.Employees.SingleAsync(employee => employee.Id == employeeId));

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        // Returned, then erased: the asset survives them, with no holder.
        await Service(owned).ReturnAsync(assetId);

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
            context.Employees.Remove(
                await context.Employees.SingleAsync(employee => employee.Id == employeeId));

            await context.SaveChangesAsync();
        }

        AssetDto? survivor = await Service(owned).GetAsync(assetId);

        Assert.NotNull(survivor);
        Assert.Equal(AssetStatus.Available, survivor.Status);
    }
}
