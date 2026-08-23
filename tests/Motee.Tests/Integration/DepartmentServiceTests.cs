using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Organisation;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class DepartmentServiceTests(PostgresFixture fixture)
{
    private static DepartmentRequest Request(
        string name = "Engineering",
        string code = "ENG",
        DepartmentStatus status = DepartmentStatus.Active) => new()
    {
        Name = name,
        Code = code,
        Description = "Builds the product",
        BudgetMonthly = 1_500_000.50m,
        Status = status,
    };

    private async Task<(Guid TenantId, ServiceProvider Provider)> ArrangeAsync()
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

        return (tenantId, provider);
    }

    private static IDepartmentService Service(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IDepartmentService>();

    [SkippableFact]
    public async Task CreatesADepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        DepartmentResult result = await Service(owned).CreateAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal("Engineering", result.Department!.Name);
        Assert.Equal("ENG", result.Department.Code);
        Assert.Equal(1_500_000.50m, result.Department.BudgetMonthly);
        Assert.Equal(DepartmentStatus.Active, result.Department.Status);
        Assert.Equal(0, result.Department.EmployeeCount);

        await using MoteeDbContext context = fixture.CreateContext(tenantId);
        Assert.Equal(tenantId, (await context.Departments.SingleAsync()).TenantId);
    }

    // The modal uppercases; the API must not depend on the client having done so.
    [SkippableFact]
    public async Task StoresTheCodeUppercased()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        DepartmentResult result = await Service(owned).CreateAsync(Request(code: "eng"));

        Assert.Equal("ENG", result.Department!.Code);
    }

    [SkippableFact]
    public async Task RefusesADuplicateName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request());
        DepartmentResult second = await Service(owned).CreateAsync(Request(code: "ENG2"));

        Assert.Equal(DepartmentOutcome.DuplicateName, second.Outcome);
    }

    [SkippableFact]
    public async Task RefusesADuplicateCode()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request());
        DepartmentResult second = await Service(owned).CreateAsync(Request(name: "Engineering II"));

        Assert.Equal(DepartmentOutcome.DuplicateCode, second.Outcome);
    }

    [SkippableFact]
    public async Task ComparesNamesWithoutRegardToCase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request());

        Assert.Equal(
            DepartmentOutcome.DuplicateName,
            (await Service(owned).CreateAsync(Request(name: "engineering", code: "ENG2"))).Outcome);
    }

    [SkippableFact]
    public async Task ListsInNameOrder()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(name: "Sales", code: "SAL"));
        await Service(owned).CreateAsync(Request(name: "Engineering", code: "ENG"));
        await Service(owned).CreateAsync(Request(name: "Finance", code: "FIN"));

        Assert.Equal(
            ["Engineering", "Finance", "Sales"],
            (await Service(owned).ListAsync()).Select(department => department.Name));
    }

    [SkippableFact]
    public async Task CountsEmployeesRatherThanStoringATotal()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        DepartmentResult created = await Service(owned).CreateAsync(Request());
        Guid departmentId = created.Department!.Id;

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
            context.Employees.AddRange(
                Employee(tenantId, departmentId, "ada@acme.com"),
                Employee(tenantId, departmentId, "bola@acme.com"));

            await context.SaveChangesAsync();
        }

        Assert.Equal(2, (await Service(owned).GetAsync(departmentId))!.EmployeeCount);
    }

    [SkippableFact]
    public async Task ResolvesTheHeadsNameFromTheEmployeeRecord()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid headId = Guid.NewGuid();

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
            Employee head = Employee(tenantId, null, "ada@acme.com");
            head.Id = headId;
            head.FirstName = "Ada";
            head.LastName = "Okafor";

            context.Employees.Add(head);
            await context.SaveChangesAsync();
        }

        DepartmentResult created = await Service(owned)
            .CreateAsync(Request() with { HeadEmployeeId = headId });

        Assert.Equal("Ada Okafor", created.Department!.HeadName);
        Assert.Equal("AO", created.Department.HeadInitials);
    }

    [SkippableFact]
    public async Task UpdatesTheEditableFields()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned).CreateAsync(Request())).Department!.Id;

        DepartmentResult updated = await Service(owned).UpdateAsync(id, new DepartmentRequest
        {
            Name = "Engineering & Data",
            Code = "ENGD",
            Description = "Now includes data",
            BudgetMonthly = 2_000_000m,
            Status = DepartmentStatus.Restructuring,
        });

        Assert.True(updated.Succeeded);
        Assert.Equal("Engineering & Data", updated.Department!.Name);
        Assert.Equal("ENGD", updated.Department.Code);
        Assert.Equal(DepartmentStatus.Restructuring, updated.Department.Status);
        Assert.NotNull(updated.Department.UpdatedAt);
    }

    [SkippableFact]
    public async Task RenamingToAnotherDepartmentsNameIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(name: "Finance", code: "FIN"));
        Guid id = (await Service(owned).CreateAsync(Request())).Department!.Id;

        Assert.Equal(
            DepartmentOutcome.DuplicateName,
            (await Service(owned).UpdateAsync(id, Request(name: "Finance"))).Outcome);
    }

    // Its own name must not count as a duplicate of itself.
    [SkippableFact]
    public async Task SavingADepartmentUnchangedIsAllowed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned).CreateAsync(Request())).Department!.Id;

        Assert.True((await Service(owned).UpdateAsync(id, Request())).Succeeded);
    }

    [SkippableFact]
    public async Task DeletesAnEmptyDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned).CreateAsync(Request())).Department!.Id;

        Assert.Equal(DepartmentOutcome.Succeeded, await Service(owned).DeleteAsync(id));
        Assert.Empty(await Service(owned).ListAsync());
    }

    // Deleting would leave employees pointing at nothing.
    [SkippableFact]
    public async Task RefusesToDeleteADepartmentWithPeopleInIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned).CreateAsync(Request())).Department!.Id;

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
            context.Employees.Add(Employee(tenantId, id, "ada@acme.com"));
            await context.SaveChangesAsync();
        }

        Assert.Equal(DepartmentOutcome.HasEmployees, await Service(owned).DeleteAsync(id));
        Assert.Single(await Service(owned).ListAsync());
    }

    [SkippableFact]
    public async Task ReportsNotFoundForAnUnknownId()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.Null(await Service(owned).GetAsync(Guid.NewGuid()));
        Assert.Equal(DepartmentOutcome.NotFound, await Service(owned).DeleteAsync(Guid.NewGuid()));
        Assert.Equal(
            DepartmentOutcome.NotFound,
            (await Service(owned).UpdateAsync(Guid.NewGuid(), Request())).Outcome);
    }

    // Two tenants may each have an Engineering / ENG.
    [SkippableFact]
    public async Task TheSameNameAndCodeAreFreeInAnotherTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request());

        Guid otherTenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = otherTenantId,
                Name = "Globex",
                Slug = $"globex-{otherTenantId:N}",
                CountryCode = CountryCode.UnitedKingdom,
            });

            await seed.SaveChangesAsync();
        }

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = otherTenantId;

        Assert.True((await Service(owned).CreateAsync(Request())).Succeeded);
        Assert.Single(await Service(owned).ListAsync());
    }

    private static Employee Employee(Guid tenantId, Guid? departmentId, string email) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        FirstName = "Test",
        LastName = "Person",
        Email = email,
        DepartmentId = departmentId,
    };
}
