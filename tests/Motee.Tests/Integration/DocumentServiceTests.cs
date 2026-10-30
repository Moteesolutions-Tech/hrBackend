using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Documents;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Documents;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The company's filing, over the top of stored_files.
//
// Written before the service was, so the expectations come from what a document
// system has to do rather than from what the code happens to do. Three rules carry
// most of the weight:
//
//   - A version is never overwritten, because "what did the policy say in March" is
//     the question this exists to answer.
//   - An acknowledgement is against a version, so reuploading asks again rather than
//     inheriting an agreement to something nobody has read.
//   - Trashing is recoverable and archiving is not the same thing.
[Collection(PostgresCollection.Name)]
public class DocumentServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _colleagueId;
    private Guid _departmentId;
    private Guid _otherDepartmentId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
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

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        IDepartmentService departments = Resolve<IDepartmentService>(provider);

        _departmentId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _otherDepartmentId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        _employeeId = await HireAsync(provider, _departmentId, "Bola", "bola@acme.com");
        _colleagueId = await HireAsync(provider, _otherDepartmentId, "Cara", "cara@acme.com");

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            (await GiveAccountAsync(_employeeId, "bola")).ToString();

        return provider;
    }

    private async Task<Guid> GiveAccountAsync(Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = handle,
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return userId;
    }

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        Guid departmentId,
        string first,
        string email) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
        })).Employee!.Id;

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    // Documents reference an already-uploaded file. Seeded directly rather than
    // through the upload endpoint, which has its own tests and would put S3 in the
    // middle of these.
    private async Task<Guid> FileAsync(string name = "handbook.pdf", long size = 1024)
    {
        Guid id = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.StoredFiles.Add(new StoredFile
        {
            Id = id,
            TenantId = _tenantId,
            Purpose = FilePurpose.CompanyDocument,
            FileName = name,
            ContentType = "application/pdf",
            SizeBytes = size,
            StorageKey = $"documents/{id}",
            UploadedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return id;
    }

    // Find or create. Several tests file more than one document, and creating the
    // folder each time trips the duplicate-name rule the service is right to have.
    private static async Task<Guid> AnyFolderAsync(ServiceProvider provider)
    {
        IDocumentService documents = Resolve<IDocumentService>(provider);

        IReadOnlyList<FolderDto> existing = await documents.FoldersAsync();

        return existing.FirstOrDefault(folder => folder.Name == "Policies")?.Id
            ?? (await documents.CreateFolderAsync(new FolderRequest { Name = "Policies" }))
                .Folder!.Id;
    }

    private async Task<CreateDocumentRequest> DocumentAsync(
        ServiceProvider provider,
        string name = "Staff Handbook",
        bool requiresAcknowledgement = false,
        AssignmentScope assignment = AssignmentScope.Specific) => new()
    {
        Name = name,
        Category = DocumentCategory.Handbook,
        FolderId = await AnyFolderAsync(provider),
        StoredFileId = await FileAsync(),
        Description = "What everybody needs to know.",
        Assignment = assignment,
        RequiresAcknowledgement = requiresAcknowledgement,
    };

    // ── folders ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AFolderCanBeCreatedAndRenamed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        FolderResult created = await documents.CreateFolderAsync(
            new FolderRequest { Name = "Policies" });

        Assert.True(created.Succeeded, created.Outcome.ToString());
        Assert.Equal(FolderType.Custom, created.Folder!.Type);
        Assert.True(created.Folder.CanDelete);

        Assert.Equal(
            "Company Policies",
            (await documents.RenameFolderAsync(created.Folder.Id, "Company Policies"))
                .Folder!.Name);
    }

    // Two folders with one name under the same parent is a filing system nobody can
    // navigate.
    [SkippableFact]
    public async Task TwoFoldersCannotShareANameUnderTheSameParent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        await documents.CreateFolderAsync(new FolderRequest { Name = "Policies" });

        Assert.Equal(
            DocumentOutcome.DuplicateFolderName,
            (await documents.CreateFolderAsync(new FolderRequest { Name = "Policies" })).Outcome);
    }

    // The same name under a different parent is ordinary filing.
    [SkippableFact]
    public async Task TheSameNameIsFineInADifferentParent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid parent = (await documents.CreateFolderAsync(
            new FolderRequest { Name = "Engineering" })).Folder!.Id;

        await documents.CreateFolderAsync(new FolderRequest { Name = "Policies" });

        Assert.True((await documents.CreateFolderAsync(
            new FolderRequest { Name = "Policies", ParentId = parent })).Succeeded);
    }

    // Deleting a folder of documents by accident is not recoverable the way
    // trashing one document is.
    [SkippableFact]
    public async Task AFolderWithDocumentsInItCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        CreateDocumentRequest request = await DocumentAsync(provider);
        await documents.CreateAsync(request);

        Assert.Equal(
            DocumentOutcome.FolderNotEmpty,
            await documents.DeleteFolderAsync(request.FolderId));
    }

    // ── documents and versions ──────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewDocumentStartsAtVersionOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DocumentResult created = await Resolve<IDocumentService>(provider)
            .CreateAsync(await DocumentAsync(provider));

        Assert.True(created.Succeeded, created.Outcome.ToString());

        DocumentVersionDto version = Assert.Single(created.Document!.Versions);

        Assert.Equal(1, version.Version);
        Assert.Equal(1024, version.SizeBytes);
        Assert.False(created.Document.IsArchived);
        Assert.False(created.Document.IsTrashed);
    }

    // "What did the policy say in March" is the question this exists to answer, so
    // a reupload adds rather than replaces.
    [SkippableFact]
    public async Task AReuploadAddsAVersionAndKeepsTheOldOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid id = (await documents.CreateAsync(await DocumentAsync(provider))).Document!.Id;

        DocumentDto after = (await documents.AddVersionAsync(id, new AddVersionRequest
        {
            StoredFileId = await FileAsync("handbook-v2.pdf", size: 2048),
            Notes = "Added the remote working section.",
        })).Document!;

        Assert.Equal(2, after.Versions.Count);

        // Newest first, so the current one is simply the first.
        Assert.Equal(2, after.Versions[0].Version);
        Assert.Equal(2048, after.Versions[0].SizeBytes);
        Assert.Equal(1, after.Versions[1].Version);
    }

    [SkippableFact]
    public async Task ADocumentCitingAFileThatDoesNotExistIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        CreateDocumentRequest request = await DocumentAsync(provider);

        Assert.Equal(
            DocumentOutcome.FileNotFound,
            (await Resolve<IDocumentService>(provider)
                .CreateAsync(request with { StoredFileId = Guid.NewGuid() })).Outcome);
    }

    // Archived is deliberate and trashed is a mistake somebody may want back. Both
    // are out of the way, and neither shows by default.
    [SkippableFact]
    public async Task ArchivedAndTrashedAreHiddenUnlessAskedFor()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid archived = (await documents.CreateAsync(
            await DocumentAsync(provider, "Old Handbook"))).Document!.Id;
        Guid trashed = (await documents.CreateAsync(
            await DocumentAsync(provider, "Wrong Upload"))).Document!.Id;
        await documents.CreateAsync(await DocumentAsync(provider, "Current Handbook"));

        await documents.ArchiveAsync(archived);
        await documents.TrashAsync(trashed);

        Assert.Equal(
            "Current Handbook",
            Assert.Single((await documents.ListAsync(new DocumentQuery())).Items).Name);

        Assert.Equal(
            2,
            (await documents.ListAsync(new DocumentQuery { IncludeArchived = true })).Items.Count);
    }

    // Recoverable, which is the whole reason trashing exists.
    [SkippableFact]
    public async Task ATrashedDocumentCanBeRestored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid id = (await documents.CreateAsync(await DocumentAsync(provider))).Document!.Id;

        Assert.True((await documents.TrashAsync(id)).Document!.IsTrashed);
        Assert.False((await documents.RestoreAsync(id)).Document!.IsTrashed);
    }

    // Derived, so it becomes true at midnight without anybody touching the row.
    [SkippableFact]
    public async Task ADocumentPastItsExpiryReadsAsExpired()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        CreateDocumentRequest request = await DocumentAsync(provider);

        DocumentDto expired = (await Resolve<IDocumentService>(provider).CreateAsync(
            request with { ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1) }))
            .Document!;

        Assert.True(expired.IsExpired);
    }

    // ── sharing and reach ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task ADocumentSharedWithSomebodyAppearsInTheirList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid id = (await documents.CreateAsync(await DocumentAsync(provider))).Document!.Id;

        await documents.ShareAsync(id, new ShareDocumentRequest
        {
            EmployeeId = _employeeId,
            Permission = DocumentPermission.Download,
        });

        DocumentDto mine = Assert.Single(await documents.MineAsync());

        Assert.Equal(id, mine.Id);
        Assert.Equal(DocumentPermission.Download, Assert.Single(mine.Shares).Permission);
    }

    // Shared with nobody and assigned to nobody means it reaches nobody — an upload
    // is not a publication.
    [SkippableFact]
    public async Task ADocumentSharedWithNobodyReachesNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        await documents.CreateAsync(await DocumentAsync(provider));

        Assert.Empty(await documents.MineAsync());
    }

    [SkippableFact]
    public async Task AGlobalDocumentReachesEverybody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        await documents.CreateAsync(
            await DocumentAsync(provider, assignment: AssignmentScope.Global));

        Assert.Single(await documents.MineAsync());
    }

    // By department, and only the right one.
    [SkippableFact]
    public async Task ADepartmentDocumentReachesThatDepartmentOnly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        CreateDocumentRequest request = await DocumentAsync(
            provider, assignment: AssignmentScope.Department);

        await documents.CreateAsync(request with
        {
            AssignedDepartmentIds = [_otherDepartmentId],
        });

        // Bola is in Engineering; the document went to Sales.
        Assert.Empty(await documents.MineAsync());
    }

    [SkippableFact]
    public async Task SharingWithTheSamePersonTwiceIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid id = (await documents.CreateAsync(await DocumentAsync(provider))).Document!.Id;

        ShareDocumentRequest share = new() { EmployeeId = _colleagueId };

        Assert.True((await documents.ShareAsync(id, share)).Succeeded);
        Assert.Equal(
            DocumentOutcome.AlreadyShared, (await documents.ShareAsync(id, share)).Outcome);
    }

    // ── acknowledgement ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SomebodyCanAcknowledgeADocumentThatAsksForIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid id = (await documents.CreateAsync(await DocumentAsync(
            provider, requiresAcknowledgement: true, assignment: AssignmentScope.Global)))
            .Document!.Id;

        Assert.False(Assert.Single(await documents.MineAsync()).AcknowledgedByMe);

        Assert.True((await documents.AcknowledgeAsync(id)).Succeeded);
        Assert.True(Assert.Single(await documents.MineAsync()).AcknowledgedByMe);
    }

    // Saying so twice would move the date and make a late acknowledgement look
    // prompt.
    [SkippableFact]
    public async Task NobodyAcknowledgesTheSameVersionTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid id = (await documents.CreateAsync(await DocumentAsync(
            provider, requiresAcknowledgement: true, assignment: AssignmentScope.Global)))
            .Document!.Id;

        await documents.AcknowledgeAsync(id);

        Assert.Equal(
            DocumentOutcome.AlreadyAcknowledged,
            (await documents.AcknowledgeAsync(id)).Outcome);
    }

    // The rule that matters most here. An acknowledgement surviving a reupload
    // would let a company claim their staff agreed to a policy none of them has
    // seen.
    [SkippableFact]
    public async Task ANewVersionAsksEverybodyAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid id = (await documents.CreateAsync(await DocumentAsync(
            provider, requiresAcknowledgement: true, assignment: AssignmentScope.Global)))
            .Document!.Id;

        await documents.AcknowledgeAsync(id);

        await documents.AddVersionAsync(
            id, new AddVersionRequest { StoredFileId = await FileAsync("v2.pdf") });

        Assert.False(Assert.Single(await documents.MineAsync()).AcknowledgedByMe);
        Assert.True((await documents.AcknowledgeAsync(id)).Succeeded);
    }

    [SkippableFact]
    public async Task AcknowledgingSomethingThatAsksForNoneIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid id = (await documents.CreateAsync(await DocumentAsync(
            provider, assignment: AssignmentScope.Global))).Document!.Id;

        Assert.Equal(
            DocumentOutcome.AcknowledgementNotRequired,
            (await documents.AcknowledgeAsync(id)).Outcome);
    }

    // "Fourteen outstanding" is a number; a list is something somebody can chase.
    [SkippableFact]
    public async Task TheReportNamesWhoHasNotAcknowledgedYet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDocumentService documents = Resolve<IDocumentService>(provider);

        Guid id = (await documents.CreateAsync(await DocumentAsync(
            provider, requiresAcknowledgement: true, assignment: AssignmentScope.Global)))
            .Document!.Id;

        await documents.AcknowledgeAsync(id);

        AcknowledgementReportDto report = (await documents.AcknowledgementsAsync(id))!;

        Assert.Equal(2, report.TotalAssigned);
        Assert.Equal("Bola Okafor", Assert.Single(report.Acknowledged).EmployeeName);
        Assert.Equal("Cara Okafor", Assert.Single(report.Outstanding));
    }
}
