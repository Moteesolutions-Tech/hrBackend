using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class BusinessUnitServiceTests(PostgresFixture fixture)
{
    private static BusinessUnitRequest Request(
        string name = "Corporate Services",
        string? code = "CORP",
        bool isActive = true) => new()
    {
        Name = name,
        Code = code,
        Description = "HR, Finance and Legal",
        IsActive = isActive,
    };

    private async Task<(IBusinessUnitService Units, IDepartmentService Departments, ServiceProvider Owned)>
        ArrangeAsync()
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

        IServiceProvider scope = provider.CreateScope().ServiceProvider;

        return (
            scope.GetRequiredService<IBusinessUnitService>(),
            scope.GetRequiredService<IDepartmentService>(),
            provider);
    }

    [SkippableFact]
    public async Task ACreatedUnitComesBackWithNoDepartments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        BusinessUnitResult result = await units.CreateAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal("Corporate Services", result.BusinessUnit!.Name);
        Assert.Equal("CORP", result.BusinessUnit.Code);
        Assert.Equal(0, result.BusinessUnit.DepartmentCount);
    }

    // Not every company codes its divisions, and requiring one makes them invent
    // placeholders that then appear in finance exports.
    [SkippableFact]
    public async Task TheCodeIsOptional()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        BusinessUnitResult result = await units.CreateAsync(Request(code: null));

        Assert.True(result.Succeeded);
        Assert.Null(result.BusinessUnit!.Code);
    }

    // "Commercial" and "commercial" are the same division to a person, and two of them
    // is two scopes nobody can tell apart on an access level form.
    [SkippableFact]
    public async Task NamesClashRegardlessOfCase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await units.CreateAsync(Request(name: "Commercial"));

        BusinessUnitResult second = await units.CreateAsync(Request(name: "commercial", code: "COMM2"));

        Assert.Equal(BusinessUnitOutcome.DuplicateName, second.Outcome);
    }

    // The name is matched with ILIKE, so wildcards in it must be compared literally or
    // a unit called "R&D 100%" would collide with everything.
    [SkippableFact]
    public async Task AWildcardInTheNameIsNotTreatedAsAPattern()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await units.CreateAsync(Request(name: "R&D 100%", code: "RD"));

        BusinessUnitResult other = await units.CreateAsync(Request(name: "Logistics", code: "LOG"));

        Assert.True(other.Succeeded);
    }

    // Renaming must be safe: an access level references the id, so who it reaches
    // cannot change because someone fixed a typo. That is the whole reason this is an
    // entity rather than a string on a department.
    [SkippableFact]
    public async Task RenamingKeepsTheSameId()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        BusinessUnitResult created = await units.CreateAsync(Request());
        Guid id = created.BusinessUnit!.Id;

        BusinessUnitResult renamed = await units.UpdateAsync(id, Request(name: "Group Services"));

        Assert.True(renamed.Succeeded);
        Assert.Equal(id, renamed.BusinessUnit!.Id);
        Assert.Equal("Group Services", renamed.BusinessUnit.Name);
    }

    [SkippableFact]
    public async Task AUnitWithDepartmentsCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, IDepartmentService departments, ServiceProvider provider) =
            await ArrangeAsync();
        await using ServiceProvider owned = provider;

        BusinessUnitResult unit = await units.CreateAsync(Request());

        await departments.CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources",
            Code = "HR",
            BusinessUnitId = unit.BusinessUnit!.Id,
        });

        Assert.Equal(
            BusinessUnitOutcome.HasDepartments,
            await units.DeleteAsync(unit.BusinessUnit.Id));
    }

    [SkippableFact]
    public async Task AnEmptyUnitCanBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        BusinessUnitResult unit = await units.CreateAsync(Request());

        Assert.Equal(
            BusinessUnitOutcome.Succeeded,
            await units.DeleteAsync(unit.BusinessUnit!.Id));

        Assert.Null(await units.GetAsync(unit.BusinessUnit.Id));
    }

    // The count drives the refusal above, so a wrong one produces a refusal the
    // interface cannot explain.
    [SkippableFact]
    public async Task TheDepartmentCountReflectsWhatIsAttached()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, IDepartmentService departments, ServiceProvider provider) =
            await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid unitId = (await units.CreateAsync(Request())).BusinessUnit!.Id;

        await departments.CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR", BusinessUnitId = unitId,
        });

        await departments.CreateAsync(new DepartmentRequest
        {
            Name = "Finance", Code = "FIN", BusinessUnitId = unitId,
        });

        // Unattached, so it must not count.
        await departments.CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" });

        Assert.Equal(2, (await units.GetAsync(unitId))!.DepartmentCount);
    }

    // A department is what links employees to a unit, so this is the assignment the
    // whole scope depends on.
    [SkippableFact]
    public async Task ADepartmentCarriesItsUnitAndResolvesTheName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, IDepartmentService departments, ServiceProvider provider) =
            await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid unitId = (await units.CreateAsync(Request())).BusinessUnit!.Id;

        DepartmentResult created = await departments.CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR", BusinessUnitId = unitId,
        });

        Assert.Equal(unitId, created.Department!.BusinessUnitId);
        Assert.Equal("Corporate Services", created.Department.BusinessUnitName);
    }

    // Moving a department between divisions is a reorganisation, not a rebuild.
    [SkippableFact]
    public async Task ADepartmentCanBeMovedBetweenUnits()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, IDepartmentService departments, ServiceProvider provider) =
            await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid first = (await units.CreateAsync(Request())).BusinessUnit!.Id;
        Guid second = (await units.CreateAsync(Request(name: "Commercial", code: "COMM"))).BusinessUnit!.Id;

        DepartmentResult created = await departments.CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR", BusinessUnitId = first,
        });

        DepartmentResult moved = await departments.UpdateAsync(
            created.Department!.Id,
            new DepartmentRequest { Name = "Human Resources", Code = "HR", BusinessUnitId = second });

        Assert.Equal(second, moved.Department!.BusinessUnitId);
        Assert.Equal(0, (await units.GetAsync(first))!.DepartmentCount);
        Assert.Equal(1, (await units.GetAsync(second))!.DepartmentCount);
    }

    // The point of the whole feature. Before this existed, DataScopeKind.BusinessUnit
    // filtered employee → department → business_unit against an empty id list and
    // matched nobody — so an admin choosing "assigned business units" produced a level
    // that opened nothing, with no error to explain it.
    [SkippableFact]
    public async Task AnAccessLevelScopedToAUnitReachesItsEmployees()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, IDepartmentService departments, ServiceProvider provider) =
            await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid corporate = (await units.CreateAsync(Request())).BusinessUnit!.Id;
        Guid commercial = (await units.CreateAsync(Request(name: "Commercial", code: "COMM")))
            .BusinessUnit!.Id;

        Guid hr = (await departments.CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR", BusinessUnitId = corporate,
        })).Department!.Id;

        Guid sales = (await departments.CreateAsync(new DepartmentRequest
        {
            Name = "Sales", Code = "SAL", BusinessUnitId = commercial,
        })).Department!.Id;

        IEmployeeService employees = owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>();

        await employees.CreateAsync(
            Employee("ada@acme.com", "Ada", hr), OnboardingMethod.Manual);

        await employees.CreateAsync(
            Employee("ben@acme.com", "Ben", sales), OnboardingMethod.Manual);

        PagedResult<EmployeeListItemDto> reached = await employees.ListAsync(new EmployeeQuery
        {
            Scope = DataScope.BusinessUnits(corporate),
        });

        Assert.Equal("Ada Okafor", Assert.Single(reached.Items).Name);
    }

    // A department with no unit belongs to no division, so no unit-scoped level should
    // see its people. Matching them would silently widen every such level.
    [SkippableFact]
    public async Task AnUnassignedDepartmentIsOutOfReachOfEveryUnit()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, IDepartmentService departments, ServiceProvider provider) =
            await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid corporate = (await units.CreateAsync(Request())).BusinessUnit!.Id;

        Guid orphan = (await departments.CreateAsync(new DepartmentRequest
        {
            Name = "Facilities", Code = "FAC",
        })).Department!.Id;

        IEmployeeService employees = owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>();

        await employees.CreateAsync(
            Employee("cara@acme.com", "Cara", orphan), OnboardingMethod.Manual);

        PagedResult<EmployeeListItemDto> reached = await employees.ListAsync(new EmployeeQuery
        {
            Scope = DataScope.BusinessUnits(corporate),
        });

        Assert.Empty(reached.Items);
    }

    private static EmployeeRequest Employee(string email, string firstName, Guid departmentId) => new()
    {
        FirstName = firstName,
        LastName = "Okafor",
        Email = email,
        Phone = string.Empty,
        JobTitle = "Analyst",
        DepartmentId = departmentId,
        EmploymentType = EmploymentType.FullTime,
        Status = EmployeeStatus.Active,
    };

    // Retired rather than removed: an access level scoped to it keeps working, so
    // deactivating cannot quietly change anyone's reach.
    [SkippableFact]
    public async Task AUnitCanBeDeactivatedWithoutBeingRemoved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (IBusinessUnitService units, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await units.CreateAsync(Request())).BusinessUnit!.Id;

        await units.UpdateAsync(id, Request(isActive: false));

        BusinessUnitDto? unit = await units.GetAsync(id);

        Assert.NotNull(unit);
        Assert.False(unit!.IsActive);
    }
}
