using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Assets;
using Motee.Domain.Assets;
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
public class EmployeeImportTests(PostgresFixture fixture)
{
    private async Task<(Guid DepartmentId, ServiceProvider Provider)> ArrangeAsync()
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

        DepartmentResult department = await provider.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" });

        return (department.Department!.Id, provider);
    }

    private static IEmployeeImportService Import(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IEmployeeImportService>();

    private static IEmployeeService Employees(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IEmployeeService>();

    // Someone already on the books, created the manual way rather than imported.
    private static EmployeeRequest Existing(Guid departmentId, string email) => new()
    {
        FirstName = "Ada",
        LastName = "Okafor",
        Email = email,
        Phone = "08012345678",
        JobTitle = "Engineer",
        DepartmentId = departmentId,
        EmploymentType = EmploymentType.FullTime,
    };

    // The department arrives by name, as it does in a spreadsheet.
    private static EmployeeImportRow Row(
        string email,
        string firstName = "Ada",
        string department = "Engineering") => new()
    {
        FirstName = firstName,
        LastName = "Okafor",
        Email = email,
        Phone = "08012345678",
        JobTitle = "Engineer",
        Department = department,
        EmploymentType = EmploymentType.FullTime,
    };

    [SkippableFact]
    public async Task ImportsEveryValidRow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("ada@acme.com"),
            Row("bola@acme.com", "Bola"),
            Row("chidi@acme.com", "Chidi"),
        ]);

        Assert.Equal(3, result.Imported);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Errors);
        Assert.Equal(3, ((await Employees(owned).ListAsync(new EmployeeQuery { Scope = DataScope.Everything })).Items).Count);
    }

    [SkippableFact]
    public async Task MarksImportedRowsAsBulk()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Import(owned).ImportAsync([Row("ada@acme.com")]);

        EmployeeListItemDto item = Assert.Single(
            (await Employees(owned).ListAsync(new EmployeeQuery { Scope = DataScope.Everything })).Items);

        EmployeeDto created = (await Employees(owned).GetAsync(item.Id))!;

        Assert.Equal(OnboardingMethod.Bulk, created.OnboardingMethod);
    }

    // 200 rows with 3 typos is the normal case. Rejecting the whole file would mean
    // fixing one cell and re-uploading everything.
    [SkippableFact]
    public async Task ValidRowsCommitEvenWhenOthersFail()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("ada@acme.com"),
            Row("bola@acme.com", "Bola", department: "Nope"),
            Row("chidi@acme.com", "Chidi"),
        ]);

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
    }

    [SkippableFact]
    public async Task ReportsTheRowNumberAndAddressThatFailed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("ada@acme.com"),
            Row("bola@acme.com", "Bola", department: "Nope"),
        ]);

        EmployeeImportError error = Assert.Single(result.Errors);

        Assert.Equal(2, error.Row);
        Assert.Equal("bola@acme.com", error.Email);
        Assert.Contains("department", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ReportsFieldValidationPerRow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("ada@acme.com") with { Email = "not-an-email" },
            Row("bola@acme.com", "Bola") with { FirstName = "" },
        ]);

        Assert.Equal(0, result.Imported);
        Assert.Equal(2, result.Failed);
        Assert.Contains("email", result.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("First name", result.Errors[1].Message, StringComparison.OrdinalIgnoreCase);
    }

    // A file that lists the same person twice: the first wins, the second is a
    // duplicate like any other.
    [SkippableFact]
    public async Task DetectsDuplicatesWithinTheSameFile()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("ada@acme.com"),
            Row("ada@acme.com", "Adaeze"),
        ]);

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.Errors[0].Row);
    }

    [SkippableFact]
    public async Task DetectsDuplicatesAgainstEmployeesAlreadyPresent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Employees(owned).CreateAsync(Existing(departmentId, "ada@acme.com"));

        EmployeeImportResult result = await Import(owned).ImportAsync(
            [Row("ada@acme.com", "Adaeze")]);

        Assert.Equal(0, result.Imported);
        Assert.Contains("email", result.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AnEmptyFileIsNotAnError()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync([]);

        Assert.Equal(0, result.Imported);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Errors);
    }

    [SkippableFact]
    public async Task RowNumbersSurviveAContiguousRunOfFailures()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("a@acme.com", "A", department: "Nope"),
            Row("b@acme.com", "B", department: "Nope"),
            Row("c@acme.com", "C"),
            Row("d@acme.com", "D", department: "Nope"),
        ]);

        Assert.Equal([1, 2, 4], result.Errors.Select(error => error.Row));
    }

    // ---- everyone imported is emailed a link to set a password ----

    [SkippableFact]
    public async Task ImportingSendsEveryoneAPasswordLink()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("ada@acme.com"),
            Row("bola@acme.com", "Bola"),
        ]);

        Assert.Equal(2, result.Imported);
        Assert.Equal(2, result.Invited);

        IReadOnlyList<PendingInvitationDto> pending = await owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeInvitationService>()
            .PendingAsync();

        Assert.Equal(2, pending.Count);
    }

    // They were added by someone else, so there is nothing for them to complete —
    // only a password to set.
    [SkippableFact]
    public async Task ThoseLinksShowNothingTheFileContained()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Import(owned).ImportAsync([Row("ada@acme.com")]);

        IEmployeeInvitationService invitations = owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeInvitationService>();

        // Reissued to get a token in hand; the purpose is what is being checked.
        Guid employeeId = (await invitations.PendingAsync()).Single().EmployeeId;
        IssueInviteResult reissued = await invitations.IssueAsync(employeeId);

        InvitationPreview preview = (await invitations.PreviewAsync(reissued.Token!))!;

        Assert.Equal(InvitationPurpose.Credentials, preview.Purpose);
        Assert.Null(preview.JobTitle);
        Assert.Null(preview.Department);
    }

    // Migrating five years of records should not email five years of leavers.
    [SkippableFact]
    public async Task ImportingCanBeSilent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned)
            .ImportAsync([Row("ada@acme.com")], sendInvitations: false);

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Invited);

        Assert.Empty(await owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeInvitationService>()
            .PendingAsync());
    }

    // Recording a leaver for history is normal. Emailing them an account is not.
    [SkippableFact]
    public async Task ALeaverInTheFileIsImportedButNotEmailed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
            [Row("gone@acme.com") with { Status = EmployeeStatus.Inactive }]);

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Invited);
    }

    // ---- resolving names, which is what a spreadsheet actually contains ----

    [SkippableFact]
    public async Task ResolvesTheDepartmentByName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Import(owned).ImportAsync([Row("ada@acme.com")]);

        EmployeeListItemDto imported = (await Employees(owned)
            .ListAsync(new EmployeeQuery { Scope = DataScope.Everything })).Items.Single();

        Assert.Equal(departmentId, imported.DepartmentId);
    }

    // Nobody types a department name the same way twice.
    [SkippableFact]
    public async Task DepartmentNamesIgnoreCaseAndSurroundingSpace()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("a@acme.com", "A", department: "  engineering "),
            Row("b@acme.com", "B", department: "ENGINEERING"),
        ]);

        Assert.Equal(2, result.Imported);
    }

    [SkippableFact]
    public async Task AnUnknownDepartmentSaysWhichOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
            [Row("ada@acme.com", department: "Enginering")]);

        Assert.Contains("Enginering", result.Errors.Single().Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ResolvesAManagerByEmail()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("boss@acme.com", "Boss"),
            Row("report@acme.com", "Report") with { Manager = "boss@acme.com" },
        ]);

        Assert.Equal(2, result.Imported);

        EmployeeListItemDto report = (await Employees(owned)
                .ListAsync(new EmployeeQuery { Scope = DataScope.Everything, Search = "report" }))
            .Items.Single();

        Assert.Equal("Boss Okafor", report.ManagerName);
    }

    // A manager listed above their reports is created by the time the report's row
    // runs, because rows commit one at a time.
    [SkippableFact]
    public async Task ResolvesAManagerByNameFromEarlierInTheSameFile()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("boss@acme.com", "Boss"),
            Row("report@acme.com", "Report") with { Manager = "Boss Okafor" },
        ]);

        Assert.Equal(2, result.Imported);
    }

    // Below their report, the manager does not exist yet — so the row fails and says
    // what to do about it.
    [SkippableFact]
    public async Task AManagerListedBelowTheirReportFails()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("report@acme.com", "Report") with { Manager = "Boss Okafor" },
            Row("boss@acme.com", "Boss"),
        ]);

        Assert.Equal(1, result.Imported);
        Assert.Contains("above their reports", result.Errors.Single().Message, StringComparison.Ordinal);
    }

    // Two people with the same name is normal. Guessing which one would quietly put
    // someone under the wrong manager.
    [SkippableFact]
    public async Task AnAmbiguousManagerNameIsRefusedRatherThanGuessed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("one@acme.com", "Ada"),
            Row("two@acme.com", "Ada"),
            Row("report@acme.com", "Report") with { Manager = "Ada Okafor" },
        ]);

        Assert.Equal(2, result.Imported);
        Assert.Contains("More than one", result.Errors.Single().Message, StringComparison.Ordinal);
    }

    // Employment type decides pay, notice and benefits. A blank column recording a
    // room full of contractors as full-time is a real harm, so it is refused rather
    // than defaulted.
    [SkippableFact]
    public async Task EmploymentTypeIsNeverGuessed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
            [Row("ada@acme.com") with { EmploymentType = null }]);

        Assert.Equal(0, result.Imported);
        Assert.Contains("Employment type", result.Errors.Single().Message, StringComparison.Ordinal);
    }

    // How someone got here is the endpoint's fact. A caller claiming to be the invite
    // route would corrupt the field that tells the three onboarding paths apart.
    [SkippableFact]
    public async Task TheCallerCannotClaimADifferentOnboardingRoute()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        // The manual path, whatever a request body might have said — there is no
        // longer a field for it to say it in.
        EmployeeResult manual = await Employees(owned)
            .CreateAsync(Existing(departmentId, "manual@acme.com"));

        await Import(owned).ImportAsync([Row("bulk@acme.com", "Bulk")]);

        EmployeeDto created = (await Employees(owned).GetAsync(manual.Employee!.Id))!;

        Assert.Equal(OnboardingMethod.Manual, created.OnboardingMethod);
    }

    // Migrating historical staff should not stall on a missing phone number.
    [SkippableFact]
    public async Task PhoneIsOptional()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
            [Row("ada@acme.com") with { Phone = null }]);

        Assert.Equal(1, result.Imported);
    }

    // These people already work here; putting them into an onboarding pipeline they
    // finished years ago would be wrong.
    [SkippableFact]
    public async Task ImportedStaffDefaultToActive()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Import(owned).ImportAsync([Row("ada@acme.com")]);

        EmployeeListItemDto imported = (await Employees(owned)
            .ListAsync(new EmployeeQuery { Scope = DataScope.Everything })).Items.Single();

        Assert.Equal(EmployeeStatus.Active, imported.Status);
    }

    [SkippableFact]
    public async Task KitOnARowIsCreatedAndAssigned()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
        [
            Row("ada@acme.com") with
            {
                AssetTag = "AST-0142",
                AssetName = "MacBook Pro 14",
                AssetCategory = "Laptop",
            },
        ]);

        Assert.Equal(1, result.Imported);

        AssetDto asset = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IAssetService>()
            .ListAsync(new AssetQuery { Scope = DataScope.Everything })).Items.Single();

        Assert.Equal("AST-0142", asset.Tag);
        Assert.Equal(AssetStatus.Assigned, asset.Status);
        Assert.Equal("Ada Okafor", asset.AssignedToName);
    }

    // A tag with no name identifies nothing, and the row should say so rather than
    // creating half an asset.
    [SkippableFact]
    public async Task AnAssetTagWithoutANameIsRejected()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeImportResult result = await Import(owned).ImportAsync(
            [Row("ada@acme.com") with { AssetTag = "AST-0142" }]);

        Assert.Equal(0, result.Imported);
        Assert.Contains("asset name", result.Errors.Single().Message, StringComparison.OrdinalIgnoreCase);
    }

    // The template and the parser drifting apart is how this shipped broken.
    [SkippableFact]
    public void TheTemplateOnlyNamesColumnsTheImportReads()
    {
        string[] headers = EmployeeImportColumns.TemplateCsv()
            .Split('\n')[0]
            .Trim()
            .Split(',');

        HashSet<string> known = [.. typeof(EmployeeImportRow)
            .GetProperties()
            .Select(property => property.Name)];

        Assert.All(headers, header =>
            Assert.Contains(header, known, StringComparer.OrdinalIgnoreCase));

        // Required columns must all be present, or the template produces rows that
        // cannot import.
        Assert.All(
            EmployeeImportColumns.All.Where(column => column.Required),
            column => Assert.Contains(column.Key, headers));
    }

    // Health data does not belong in a spreadsheet passed around by email, and it
    // needs a permission the person running an import may not hold.
    [SkippableFact]
    public void TheTemplateCarriesNoMedicalColumns()
    {
        string template = EmployeeImportColumns.TemplateCsv();

        Assert.DoesNotContain("allergies", template, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("medication", template, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("conditions", template, StringComparison.OrdinalIgnoreCase);
    }
}
