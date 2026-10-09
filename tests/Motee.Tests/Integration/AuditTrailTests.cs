using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Audit;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Organisation;
using Motee.Domain.Audit;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The trail is written by a SaveChanges interceptor rather than by each service, which
// is what makes a new module audited the day its entity exists. These pin the two
// properties that has to keep: it captures without being asked, and it never captures
// what it must not.
[Collection(PostgresCollection.Name)]
public class AuditTrailTests(PostgresFixture fixture)
{
    private Guid _tenantId;

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

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        return provider;
    }

    private async Task<IReadOnlyList<AuditEntry>> EntriesAsync()
    {
        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        return await context.AuditEntries.AsNoTracking()
            .OrderBy(entry => entry.CreatedAt)
            .ToListAsync();
    }

    private static IDepartmentService Departments(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IDepartmentService>();

    // Nothing in DepartmentService mentions auditing. That is the point: the module was
    // written before the trail existed and is audited anyway.
    [SkippableFact]
    public async Task CreatingSomethingIsRecordedWithoutTheServiceAskingForIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        AuditEntry entry = Assert.Single(await EntriesAsync());

        Assert.Equal(AuditAction.Create, entry.Action);
        Assert.Equal("Department", entry.EntityType);
        Assert.Equal("organization.departments", entry.Module);
    }

    [SkippableFact]
    public async Task AnUpdateRecordsWhatChanged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DepartmentResult created = await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        await Departments(provider).UpdateAsync(
            created.Department!.Id,
            new DepartmentRequest { Name = "People", Code = "HR" });

        AuditEntry update = (await EntriesAsync()).Last();

        Assert.Equal(AuditAction.Update, update.Action);
        Assert.NotNull(update.Changes);
        Assert.Contains("Human Resources", update.Changes, StringComparison.Ordinal);
        Assert.Contains("People", update.Changes, StringComparison.Ordinal);
    }

    // EF marks an entity modified when it is attached and written back unchanged. A
    // trail full of "updated, nothing different" is one nobody reads, so those produce
    // no row at all.
    [SkippableFact]
    public async Task SavingWithoutChangingAnythingRecordsNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DepartmentRequest request = new() { Name = "Human Resources", Code = "HR" };

        DepartmentResult created = await Departments(provider).CreateAsync(request);

        int before = (await EntriesAsync()).Count;

        await Departments(provider).UpdateAsync(created.Department!.Id, request);

        Assert.Equal(before, (await EntriesAsync()).Count);
    }

    [SkippableFact]
    public async Task ADeleteIsRecorded()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DepartmentResult created = await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        await Departments(provider).DeleteAsync(created.Department!.Id);

        Assert.Equal(AuditAction.Delete, (await EntriesAsync()).Last().Action);
    }

    // The entity id is what makes "show me this record's history" possible, and it is
    // the query an auditor actually runs.
    [SkippableFact]
    public async Task TheRecordCanBeTracedByItsId()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DepartmentResult created = await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        Guid id = created.Department!.Id;

        await Departments(provider).UpdateAsync(
            id, new DepartmentRequest { Name = "People", Code = "HR" });

        IAuditTrail audit = provider.CreateScope().ServiceProvider
            .GetRequiredService<IAuditTrail>();

        PagedResult<AuditEntryDto> history = await audit.ListAsync(new AuditQuery { EntityId = id });

        Assert.Equal(2, history.TotalItems);
    }

    // The interceptor runs inside base.SaveChanges, after the context has already
    // stamped tenants — so it stamps its own. Getting this wrong writes rows with an
    // empty tenant that no query filter will ever return.
    [SkippableFact]
    public async Task EntriesBelongToTheTenantThatCausedThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        await using MoteeDbContext unfiltered = fixture.CreateContext();

        AuditEntry entry = Assert.Single(unfiltered.AuditEntries.IgnoreQueryFilters().ToList());

        Assert.Equal(_tenantId, entry.TenantId);

        // And the filtered context can see it, which is the same thing said from the
        // side that matters to the screen.
        Assert.Single(await EntriesAsync());
    }

    [SkippableFact]
    public async Task ExplicitEventsCanBeRecordedForThingsThatWriteNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAuditTrail>()
            .Record(AuditAction.Login, "admin.access-levels", "Signed in");

        await scope.ServiceProvider.GetRequiredService<MoteeDbContext>()
            .SaveChangesAsync();

        AuditEntry entry = Assert.Single(await EntriesAsync());

        Assert.Equal(AuditAction.Login, entry.Action);
        Assert.Equal("Signed in", entry.Description);
    }

    // The trail must never become the place where special-category data is readable
    // without the permission that guards the original. That an employee's medical
    // record changed is auditable; what it said is not.
    [SkippableFact]
    public async Task MedicalChangesRecordThatItHappenedAndNeverWhat()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await SeedEmployeeAsync();

        // Through the container, not fixture.CreateContext. That builds a context
        // directly with no interceptor — deliberately, so seeding does not fill the
        // trail with setup noise — which also means it audits nothing.
        using (IServiceScope scope = provider.CreateScope())
        {
            MoteeDbContext context = scope.ServiceProvider.GetRequiredService<MoteeDbContext>();

            context.EmployeeMedical.Add(new EmployeeMedical
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                EmployeeId = employeeId,
                Conditions = "Type 1 diabetes",
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await context.SaveChangesAsync();
        }

        using (IServiceScope scope = provider.CreateScope())
        {
            MoteeDbContext context = scope.ServiceProvider.GetRequiredService<MoteeDbContext>();

            EmployeeMedical medical = context.EmployeeMedical.First();
            medical.Conditions = "Type 2 diabetes";
            await context.SaveChangesAsync();
        }

        IReadOnlyList<AuditEntry> medicalEntries =
            [.. (await EntriesAsync()).Where(entry => entry.EntityType == "EmployeeMedical")];

        // Both the create and the update are recorded — you can see that someone
        // touched it, and when.
        Assert.Equal(2, medicalEntries.Count);

        // And neither carries the content.
        foreach (AuditEntry entry in medicalEntries)
        {
            Assert.Null(entry.Changes);
        }
    }

    // Same rule, applied to a value that would otherwise be perfectly serialisable:
    // a password hash is not a field worth diffing anywhere.
    [SkippableFact]
    public async Task RedactedFieldsAreNeverWrittenAsValues()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        using (IServiceScope scope = provider.CreateScope())
        {
            MoteeDbContext context = scope.ServiceProvider.GetRequiredService<MoteeDbContext>();

            context.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                FirstName = "Ada",
                LastName = "Okafor",
                Email = "ada@acme.com",
                NormalizedEmail = "ADA@ACME.COM",
                PasswordHash = "hash-before",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await context.SaveChangesAsync();
        }

        using (IServiceScope scope = provider.CreateScope())
        {
            MoteeDbContext context = scope.ServiceProvider.GetRequiredService<MoteeDbContext>();

            Motee.Infrastructure.Identity.ApplicationUser user = context.Users.First();
            user.PasswordHash = "hash-after";
            await context.SaveChangesAsync();
        }

        AuditEntry update = (await EntriesAsync())
            .Last(entry => entry.EntityType == "ApplicationUser");

        Assert.NotNull(update.Changes);
        Assert.DoesNotContain("hash-before", update.Changes, StringComparison.Ordinal);
        Assert.DoesNotContain("hash-after", update.Changes, StringComparison.Ordinal);
        Assert.Contains("[redacted]", update.Changes, StringComparison.Ordinal);
    }

    private async Task<Guid> SeedEmployeeAsync()
    {
        Guid employeeId = Guid.NewGuid();

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        context.Employees.Add(new Motee.Domain.Employees.Employee
        {
            Id = employeeId,
            TenantId = _tenantId,
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            Status = Motee.Domain.Employees.EmployeeStatus.Active,
            OnboardingMethod = Motee.Domain.Employees.OnboardingMethod.Manual,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        return employeeId;
    }

    // Signing in writes no entity, so nothing can infer it from a change tracker — and
    // it is the first entry an auditor asks for.
    [SkippableFact]
    public async Task SigningInIsRecorded()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
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

        _tenantId = registration.TenantId;

        await using ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        // Registration leaves the address unconfirmed, so sign-in is refused before the
        // password is even considered. Confirming it here is what makes this a test of
        // the audit trail rather than of the verification gate.
        await using (MoteeDbContext context = fixture.CreateContext(_tenantId))
        {
            Motee.Infrastructure.Identity.ApplicationUser user = context.Users.First();
            user.EmailConfirmed = true;
            await context.SaveChangesAsync();
        }

        ILoginService login = provider.CreateScope().ServiceProvider
            .GetRequiredService<ILoginService>();

        await login.LoginAsync(new LoginRequest
        {
            Email = "ada@acme.com",
            Password = "correct-horse",
            IpAddress = "10.0.0.1",
        });

        AuditEntry entry = Assert.Single(
            await EntriesAsync(),
            candidate => candidate.Action == AuditAction.Login);

        Assert.Equal("Signed in", entry.Description);
        Assert.Equal(registration.UserId, entry.EntityId);
    }

    // A wrong password against a real account is the other half, and the one that
    // matters when somebody is being probed.
    [SkippableFact]
    public async Task AFailedSignInIsRecorded()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
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

        _tenantId = registration.TenantId;

        await using ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        ILoginService login = provider.CreateScope().ServiceProvider
            .GetRequiredService<ILoginService>();

        await login.LoginAsync(new LoginRequest
        {
            Email = "ada@acme.com",
            Password = "wrong",
            IpAddress = "10.0.0.1",
        });

        AuditEntry entry = Assert.Single(
            await EntriesAsync(),
            candidate => candidate.Action == AuditAction.Login);

        Assert.Contains("Failed sign-in", entry.Description, StringComparison.Ordinal);
    }

    // The route template, not the resolved path: "employees/{id}" groups, while
    // "employees/9f3c…" gives every record its own value and makes the filter useless.
    [SkippableFact]
    public async Task TheRequestThatCausedTheChangeIsRecorded()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PostgresFixture.StubRequestContext context = (PostgresFixture.StubRequestContext)
            provider.GetRequiredService<Motee.Application.Common.IRequestContext>();

        context.Endpoint = "api/v1/departments";
        context.HttpMethod = "POST";

        await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        AuditEntry entry = Assert.Single(await EntriesAsync());

        Assert.Equal("api/v1/departments", entry.Endpoint);
        Assert.Equal("POST", entry.HttpMethod);
    }

    // The three filters the screen offers. Status is the one that was quietly broken:
    // the column was read but never written, so filtering by it always found nothing.
    [SkippableFact]
    public async Task EntriesCanBeFilteredByAction()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DepartmentResult created = await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        await Departments(provider).UpdateAsync(
            created.Department!.Id, new DepartmentRequest { Name = "People", Code = "HR" });

        IAuditTrail audit = Audit(provider);

        Assert.Equal(1, (await audit.ListAsync(new AuditQuery { Action = AuditAction.Create })).TotalItems);
        Assert.Equal(1, (await audit.ListAsync(new AuditQuery { Action = AuditAction.Update })).TotalItems);
    }

    [SkippableFact]
    public async Task EntriesCanBeFilteredByModule()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        IAuditTrail audit = Audit(provider);

        Assert.Equal(
            1,
            (await audit.ListAsync(new AuditQuery { Module = "organization.departments" })).TotalItems);

        Assert.Equal(
            0,
            (await audit.ListAsync(new AuditQuery { Module = "operations.assets" })).TotalItems);
    }

    // A change entry only exists when the change committed, so it carries a success
    // status. Without one written, the screen's 2xx/4xx/5xx filter matched nothing.
    [SkippableFact]
    public async Task EntriesCanBeFilteredByStatusClass()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PostgresFixture.StubRequestContext context = (PostgresFixture.StubRequestContext)
            provider.GetRequiredService<Motee.Application.Common.IRequestContext>();

        context.Endpoint = "api/v1/departments";
        context.HttpMethod = "POST";

        await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        IAuditTrail audit = Audit(provider);

        Assert.Equal(1, (await audit.ListAsync(new AuditQuery { StatusClass = "2xx" })).TotalItems);
        Assert.Equal(0, (await audit.ListAsync(new AuditQuery { StatusClass = "4xx" })).TotalItems);
    }

    // Background work has no request and therefore no HTTP status. Inventing one would
    // put a job in the "2xx" bucket and make the filter lie.
    [SkippableFact]
    public async Task WorkWithNoRequestCarriesNoStatus()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // Endpoint left null, which is what a Hangfire job looks like.
        await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        Assert.Null(Assert.Single(await EntriesAsync()).HttpStatus);
    }

    // The dropdowns are served, not hard-coded: the module list grows with every module
    // that ships, and one maintained in the client goes stale silently.
    [SkippableFact]
    public async Task TheCatalogueOffersOnlyWhatCanBeFound()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        AuditCatalogueDto catalogue = await Audit(provider).CatalogueAsync();

        // Every action the system can record, so the filter is complete from day one.
        Assert.Contains("create", catalogue.Actions);
        Assert.Contains("login", catalogue.Actions);

        // Modules come from the same catalogue the access levels use, so the filter is
        // stable rather than growing as data arrives — and a module with no entries is
        // offered, because "nothing has happened here" is an answer.
        Assert.Contains("organization.departments", catalogue.Modules);
        Assert.Contains("time-payroll.leave", catalogue.Modules);
    }

    // AuditPolicy falls back to a kebab-case name for an entity nobody has mapped yet.
    // Those entries must stay findable, so the catalogue is the union rather than the
    // catalogue alone — otherwise a new module's history is invisible to the filter
    // until someone remembers to name it.
    [SkippableFact]
    public async Task ModulesRecordedButNotInTheCatalogueAreStillOffered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        using (IServiceScope scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IAuditTrail>()
                .Record(AuditAction.Create, "not-a-real-module", "Something new happened");

            await scope.ServiceProvider.GetRequiredService<MoteeDbContext>()
                .SaveChangesAsync();
        }

        AuditCatalogueDto catalogue = await Audit(provider).CatalogueAsync();

        Assert.Contains("not-a-real-module", catalogue.Modules);
    }

    private static IAuditTrail Audit(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IAuditTrail>();

    [SkippableFact]
    public async Task TheTrailDoesNotAuditItself()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await Departments(provider).CreateAsync(new DepartmentRequest
        {
            Name = "Human Resources", Code = "HR",
        });

        // One entry for the department, and none describing the entry itself.
        Assert.Single(await EntriesAsync());
        Assert.DoesNotContain(
            await EntriesAsync(),
            entry => entry.EntityType == nameof(AuditEntry));
    }
}
