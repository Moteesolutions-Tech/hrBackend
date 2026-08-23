using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The Add New Employee wizard posts once, at Review, carrying every step. These
// cover the three steps that live in their own tables.
[Collection(PostgresCollection.Name)]
public class EmployeeProfileBlockTests(PostgresFixture fixture)
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

    private static IEmployeeService Service(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IEmployeeService>();

    private static EmployeeRequest Request(
        Guid departmentId,
        BankDetailsRequest? bank = null,
        IdentityDocumentsRequest? documents = null,
        MedicalRequest? medical = null) => new()
    {
        FirstName = "Ada",
        LastName = "Okafor",
        Email = "ada@acme.com",
        Phone = "08012345678",
        JobTitle = "Engineer",
        DepartmentId = departmentId,
        EmploymentType = EmploymentType.FullTime,
        BankDetails = bank,
        IdentityDocuments = documents,
        Medical = medical,
    };

    private static BankDetailsRequest Bank() => new()
    {
        BankName = "First Bank of Nigeria",
        AccountNumber = "0123456789",
        AccountHolderName = "Ada Okafor",
    };

    private static IdentityDocumentsRequest Documents() => new()
    {
        NationalIdNumber = "01234567890",
        TaxIdNumber = "TIN-99",
        PassportNumber = "A01234567",
        PassportExpiry = new DateOnly(2030, 6, 1),
        PassportIssuingCountry = "Nigeria",
    };

    private static MedicalRequest Medical() => new()
    {
        Allergies = "Peanuts, Penicillin",
        Conditions = "Asthma",
    };

    [SkippableFact]
    public async Task TheWizardCommitsEveryStepAtOnce()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeResult result = await Service(owned)
            .CreateAsync(Request(departmentId, Bank(), Documents(), Medical()));

        Assert.True(result.Succeeded, result.Outcome.ToString());
        Assert.Equal("0123456789", result.Employee!.BankDetails!.AccountNumber);
        Assert.Equal("01234567890", result.Employee.IdentityDocuments!.NationalIdNumber);
        Assert.Equal(
            "Peanuts, Penicillin",
            (await Service(owned).GetMedicalAsync(result.Employee.Id))!.Allergies);
    }

    // Health data must not ride along with the profile every Line Manager can open.
    [SkippableFact]
    public async Task MedicalIsNotPartOfTheEmployeeRecord()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned)
            .CreateAsync(Request(departmentId, medical: Medical()))).Employee!.Id;

        EmployeeDto employee = (await Service(owned).GetAsync(id))!;

        Assert.DoesNotContain(
            "Asthma",
            System.Text.Json.JsonSerializer.Serialize(employee),
            StringComparison.OrdinalIgnoreCase);
    }

    // Leaving a step out of the request is not the same as emptying it. HR editing
    // someone's job title must not wipe their bank details.
    [SkippableFact]
    public async Task AnAbsentBlockLeavesWhatIsStoredAlone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned)
            .CreateAsync(Request(departmentId, Bank(), Documents(), Medical()))).Employee!.Id;

        EmployeeResult updated = await Service(owned)
            .UpdateAsync(id, Request(departmentId) with { JobTitle = "Staff Engineer" });

        Assert.Equal("Staff Engineer", updated.Employee!.JobTitle);
        Assert.Equal("0123456789", updated.Employee.BankDetails!.AccountNumber);
        Assert.Equal("Asthma", (await Service(owned).GetMedicalAsync(id))!.Conditions);
    }

    // Sending the step with everything blank is how someone clears it.
    [SkippableFact]
    public async Task AnEmptyBlockClearsWhatIsStored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned)
            .CreateAsync(Request(departmentId, Bank()))).Employee!.Id;

        EmployeeResult updated = await Service(owned)
            .UpdateAsync(id, Request(departmentId, bank: new BankDetailsRequest()));

        Assert.NotNull(updated.Employee!.BankDetails);
        Assert.Null(updated.Employee.BankDetails.AccountNumber);
    }

    [SkippableFact]
    public async Task EditingABlockReplacesItRatherThanAddingASecondRow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned)
            .CreateAsync(Request(departmentId, Bank()))).Employee!.Id;

        await Service(owned).UpdateAsync(id, Request(departmentId,
            bank: Bank() with { AccountNumber = "9876543210" }));

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Assert.Equal("9876543210",
            (await context.EmployeeBankDetails.SingleAsync()).AccountNumber);
    }

    [SkippableFact]
    public async Task AnEmployeeWithNothingRecordedHasNoBlocks()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeDto employee = (await Service(owned).CreateAsync(Request(departmentId))).Employee!;

        Assert.Null(employee.BankDetails);
        Assert.Null(employee.IdentityDocuments);
        Assert.Null(await Service(owned).GetMedicalAsync(employee.Id));
    }

    // The blocks carry the tenant like everything else, or one company's export or
    // lookup could reach another's bank details.
    [SkippableFact]
    public async Task BlocksAreStampedWithTheTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId, Bank(), Documents(), Medical()));

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Assert.Equal(tenantId, (await context.EmployeeBankDetails.SingleAsync()).TenantId);
        Assert.Equal(tenantId, (await context.EmployeeIdentityDocuments.SingleAsync()).TenantId);
        Assert.Equal(tenantId, (await context.EmployeeMedical.SingleAsync()).TenantId);
    }

    // Another company holding the same employee id must still see nothing.
    [SkippableFact]
    public async Task BlocksAreInvisibleToAnotherTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned)
            .CreateAsync(Request(departmentId, Bank(), medical: Medical()))).Employee!.Id;

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = Guid.NewGuid();

        Assert.Null(await Service(owned).GetAsync(id));
        Assert.Null(await Service(owned).GetMedicalAsync(id));
    }

    [SkippableFact]
    public async Task InitialsAndEmergencyEmailAreStored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeResult result = await Service(owned).CreateAsync(Request(departmentId) with
        {
            MiddleName = "Ngozi",
            Initials = "ANO",
            EmergencyContactEmail = "bola@example.com",
        });

        Assert.Equal("ANO", result.Employee!.Initials);
        Assert.Equal("bola@example.com", result.Employee.EmergencyContactEmail);
    }
}
