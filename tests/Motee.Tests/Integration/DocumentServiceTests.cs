using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Documents;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;
using Motee.Domain.Documents;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Identity;
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

        AcknowledgementReportDto report =
            (await documents.AcknowledgementsAsync(id, Viewer.Everything))!;

        Assert.Equal(2, report.TotalAssigned);
        Assert.Equal("Bola Okafor", Assert.Single(report.Acknowledged).EmployeeName);
        Assert.Equal("Cara Okafor", Assert.Single(report.Outstanding));
    }

    // ── self-service uploads ────────────────────────────────────────────────
    //
    // The rule running through all of this: /documents/mine carries two different
    // things — what HR shared or assigned, and what the employee uploaded — and only
    // the second is theirs to move, bin or re-file. Without that, somebody could bin
    // the staff handbook off their own file or move their contract where HR cannot
    // find it.

    private async Task<Guid> UploadMineAsync(
        ServiceProvider provider,
        string name = "First aid certificate",
        Guid? folderId = null)
    {
        DocumentResult uploaded = await Resolve<IDocumentService>(provider).UploadMineAsync(
            new SelfUploadRequest
            {
                Name = name,
                FileId = await FileAsync($"{name}.pdf"),
                FolderId = folderId,
            });

        Assert.True(uploaded.Succeeded, uploaded.Outcome.ToString());

        return uploaded.Document!.Id;
    }

    [SkippableFact]
    public async Task AnUploadLandsAwaitingReviewAndNotInTheLibrary()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        // Theirs, and visible to them straight away.
        DocumentDto mine = Assert.Single(
            await Resolve<IDocumentService>(provider).MineAsync(),
            document => document.Id == id);

        Assert.Equal(DocumentReviewStatus.AwaitingReview, mine.ReviewStatus);
        Assert.Equal(_employeeId, mine.UploadedByEmployeeId);
        Assert.True(mine.IsMine);

        // And deliberately not in the company's filing. This is the whole point of the
        // queue: without it an employee could put anything on the record by uploading
        // it, and HR would find it already there.
        PagedResult<DocumentDto> library =
            await Resolve<IDocumentService>(provider).ListAsync(new DocumentQuery());

        Assert.DoesNotContain(library.Items, document => document.Id == id);

        // It is in the review queue instead.
        PagedResult<DocumentDto> queue =
            await Resolve<IDocumentService>(provider).ReviewsAsync(new PagedQuery());

        Assert.Equal(id, Assert.Single(queue.Items).Id);
    }

    [SkippableFact]
    public async Task AcceptingAnUploadFilesItOntoTheRecord()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        DocumentResult accepted = await Resolve<IDocumentService>(provider)
            .AcceptUploadAsync(id, "Checked against the original.");

        Assert.True(accepted.Succeeded, accepted.Outcome.ToString());
        Assert.Equal(DocumentReviewStatus.Accepted, accepted.Document!.ReviewStatus);

        // Now part of the library, which is what accepting means.
        PagedResult<DocumentDto> library =
            await Resolve<IDocumentService>(provider).ListAsync(new DocumentQuery());

        Assert.Contains(library.Items, document => document.Id == id);

        // And out of the queue.
        Assert.Empty((await Resolve<IDocumentService>(provider)
            .ReviewsAsync(new PagedQuery())).Items);
    }

    [SkippableFact]
    public async Task ARejectedUploadStaysVisibleToWhoeverSentIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        DocumentResult rejected = await Resolve<IDocumentService>(provider)
            .RejectUploadAsync(id, "The scan is unreadable — please send a clearer one.");

        Assert.True(rejected.Succeeded, rejected.Outcome.ToString());

        DocumentDto mine = Assert.Single(
            await Resolve<IDocumentService>(provider).MineAsync(),
            document => document.Id == id);

        // Still theirs to see, with the reason. Hiding it would leave them unable to
        // tell a refusal from an upload that never arrived.
        Assert.Equal(DocumentReviewStatus.Rejected, mine.ReviewStatus);
        Assert.Equal(
            "The scan is unreadable — please send a clearer one.", mine.ReviewNote);

        // Not in the library, and not back in the queue.
        Assert.DoesNotContain(
            (await Resolve<IDocumentService>(provider).ListAsync(new DocumentQuery())).Items,
            document => document.Id == id);

        Assert.Empty((await Resolve<IDocumentService>(provider)
            .ReviewsAsync(new PagedQuery())).Items);
    }

    [SkippableFact]
    public async Task RejectingWithoutAReasonIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        Assert.Equal(
            DocumentOutcome.ReasonRequired,
            (await Resolve<IDocumentService>(provider).RejectUploadAsync(id, "  ")).Outcome);
    }

    [SkippableFact]
    public async Task DecidingTwiceIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        Assert.True((await Resolve<IDocumentService>(provider).AcceptUploadAsync(id)).Succeeded);

        Assert.Equal(
            DocumentOutcome.AlreadyReviewed,
            (await Resolve<IDocumentService>(provider).AcceptUploadAsync(id)).Outcome);
    }

    // HR filing a document is not a request for permission, so there is nothing to
    // review — and a decision on one would record a sign-off nobody asked for.
    [SkippableFact]
    public async Task ADocumentHrFiledCannotBeReviewed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DocumentResult filed = await Resolve<IDocumentService>(provider).CreateAsync(
            new CreateDocumentRequest
            {
                Name = "Staff handbook",
                Category = DocumentCategory.Policy,
                FolderId = await AnyFolderAsync(provider),
                StoredFileId = await FileAsync(),
            });

        Assert.True(filed.Succeeded, filed.Outcome.ToString());
        Assert.Null(filed.Document!.ReviewStatus);

        Assert.Equal(
            DocumentOutcome.NotAwaitingReview,
            (await Resolve<IDocumentService>(provider)
                .AcceptUploadAsync(filed.Document.Id)).Outcome);
    }

    // ── ownership ───────────────────────────────────────────────────────────

    // The rule that matters most. An employee may bin their own upload and nothing
    // else — a document HR assigned is theirs to read, not to remove from their file.
    [SkippableFact]
    public async Task AnEmployeeCannotBinADocumentHrAssignedToThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DocumentResult handbook = await Resolve<IDocumentService>(provider).CreateAsync(
            new CreateDocumentRequest
            {
                Name = "Staff handbook",
                Category = DocumentCategory.Policy,
                FolderId = await AnyFolderAsync(provider),
                StoredFileId = await FileAsync(),
                Assignment = AssignmentScope.Global,
            });

        Assert.True(handbook.Succeeded, handbook.Outcome.ToString());

        // It reaches them — that is the point of a global assignment.
        DocumentDto seen = Assert.Single(
            await Resolve<IDocumentService>(provider).MineAsync(),
            document => document.Id == handbook.Document!.Id);

        // And the response says plainly that it is not theirs to touch.
        Assert.False(seen.IsMine);

        Assert.Equal(
            DocumentOutcome.NotYours,
            (await Resolve<IDocumentService>(provider)
                .TrashMineAsync(handbook.Document!.Id)).Outcome);
    }

    [SkippableFact]
    public async Task AnEmployeeCanBinAndRestoreTheirOwnUpload()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        DocumentResult binned = await Resolve<IDocumentService>(provider).TrashMineAsync(id);

        Assert.True(binned.Succeeded, binned.Outcome.ToString());
        Assert.True(binned.Document!.IsTrashed);

        DocumentResult restored = await Resolve<IDocumentService>(provider)
            .RestoreMineAsync(id);

        Assert.True(restored.Succeeded, restored.Outcome.ToString());
        Assert.False(restored.Document!.IsTrashed);
    }

    // The bin needs a screen of its own. /documents/mine lists what the employee can
    // see, and a binned document is deliberately not among them — so until this read
    // existed, binning something hid it with no way back and RestoreMineAsync could not
    // be reached at all.
    [SkippableFact]
    public async Task ABinnedUploadLeavesTheDrawerAndAppearsInTheBin()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        Assert.Contains(
            id,
            (await Resolve<IDocumentService>(provider).MineAsync()).Select(d => d.Id));

        Assert.Empty(await Resolve<IDocumentService>(provider).MyTrashAsync());

        Assert.True((await Resolve<IDocumentService>(provider).TrashMineAsync(id)).Succeeded);

        Assert.DoesNotContain(
            id,
            (await Resolve<IDocumentService>(provider).MineAsync()).Select(d => d.Id));

        DocumentDto binned = Assert.Single(
            await Resolve<IDocumentService>(provider).MyTrashAsync());

        Assert.Equal(id, binned.Id);
        Assert.True(binned.IsTrashed);
    }

    // HR's filing is not theirs to bin, so it can never be in their bin either.
    [SkippableFact]
    public async Task TheBinHoldsOnlyTheirOwnUploads()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await UploadMineAsync(provider);

        // Act as Cara, who uploaded nothing.
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            (await GiveAccountAsync(_colleagueId, "cara")).ToString();

        Assert.Empty(await Resolve<IDocumentService>(provider).MyTrashAsync());
    }

    [SkippableFact]
    public async Task AnEmployeeCanEmptyTheirOwnBin()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        Assert.True((await Resolve<IDocumentService>(provider).TrashMineAsync(id)).Succeeded);

        DocumentResult deleted = await Resolve<IDocumentService>(provider).DeleteMineAsync(id);

        Assert.True(deleted.Succeeded, deleted.Outcome.ToString());
        Assert.Equal(id, deleted.Document!.Id);

        Assert.Empty(await Resolve<IDocumentService>(provider).MyTrashAsync());

        // Gone rather than hidden: a second delete finds nothing.
        Assert.Equal(
            DocumentOutcome.NotFound,
            (await Resolve<IDocumentService>(provider).DeleteMineAsync(id)).Outcome);
    }

    // The bin is the step before, deliberately: one click must not destroy a document
    // irrecoverably.
    [SkippableFact]
    public async Task AnUploadStillInTheDrawerCannotBeDeletedForGood()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        Assert.Equal(
            DocumentOutcome.NotTrashed,
            (await Resolve<IDocumentService>(provider).DeleteMineAsync(id)).Outcome);

        // Still there, so the refusal cost nothing.
        Assert.Contains(
            id,
            (await Resolve<IDocumentService>(provider).MineAsync()).Select(d => d.Id));
    }

    [SkippableFact]
    public async Task AnEmployeeCannotDeleteAColleaguesUpload()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        Assert.True((await Resolve<IDocumentService>(provider).TrashMineAsync(id)).Succeeded);

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            (await GiveAccountAsync(_colleagueId, "cara")).ToString();

        // Not found rather than forbidden, matching the rest of self-service: telling
        // them an id exists but is none of their business is itself a fact.
        Assert.Equal(
            DocumentOutcome.NotYours,
            (await Resolve<IDocumentService>(provider).DeleteMineAsync(id)).Outcome);
    }

    [SkippableFact]
    public async Task AnEmployeeCannotTouchAColleaguesUpload()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await UploadMineAsync(provider);

        // Act as Cara, who uploaded nothing.
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            (await GiveAccountAsync(_colleagueId, "cara")).ToString();

        Assert.Equal(
            DocumentOutcome.NotYours,
            (await Resolve<IDocumentService>(provider).TrashMineAsync(id)).Outcome);

        Assert.Equal(
            DocumentOutcome.NotYours,
            (await Resolve<IDocumentService>(provider).MoveMineAsync(id, null)).Outcome);
    }

    // ── their own drawers ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnEmployeeKeepsTheirOwnFolders()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        FolderResult made = await Resolve<IDocumentService>(provider).SaveMyFolderAsync(
            null, new MyFolderRequest { Name = "Certificates", Colour = "emerald" });

        Assert.True(made.Succeeded, made.Outcome.ToString());
        Assert.Equal("Certificates", made.Folder!.Name);
        Assert.Equal("emerald", made.Folder.Colour);
        Assert.Equal(FolderType.Personal, made.Folder.Type);

        // Their list shows theirs and nobody else's: the company's folders are a
        // separate tree, reached through the documents they have been given.
        //
        // Two of them — their drawer, created the moment anything needed one, and the
        // folder they just made inside it. The drawer is on the list because it holds
        // documents and the screen has to be able to show them.
        IReadOnlyList<FolderDto> mine =
            await Resolve<IDocumentService>(provider).MyFoldersAsync();

        Assert.Equal(2, mine.Count);

        FolderDto drawer = Assert.Single(mine, folder => folder.ParentId == null);
        FolderDto made2 = Assert.Single(mine, folder => folder.Id == made.Folder.Id);

        Assert.Equal("My documents", drawer.Name);

        // Inside the drawer, not beside it — one root per person, so "no folder
        // chosen" has exactly one meaning.
        Assert.Equal(drawer.Id, made2.ParentId);
    }

    [SkippableFact]
    public async Task AColleaguesFolderIsNotMineToRenameOrDelete()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid folderId = (await Resolve<IDocumentService>(provider).SaveMyFolderAsync(
            null, new MyFolderRequest { Name = "Certificates" })).Folder!.Id;

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            (await GiveAccountAsync(_colleagueId, "cara")).ToString();

        // Cara has no drawer yet, so no folders at all — and certainly not Bola's.
        Assert.Empty(await Resolve<IDocumentService>(provider).MyFoldersAsync());

        Assert.Equal(
            DocumentOutcome.NotYours,
            (await Resolve<IDocumentService>(provider).SaveMyFolderAsync(
                folderId, new MyFolderRequest { Name = "Mine now" })).Outcome);

        Assert.Equal(
            DocumentOutcome.NotYours,
            await Resolve<IDocumentService>(provider).DeleteMyFolderAsync(folderId));
    }

    // Emptied first, like a company folder. Deleting a drawer of documents by accident
    // is not recoverable the way binning one document is.
    [SkippableFact]
    public async Task ADrawerWithAnythingInItCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid folderId = (await Resolve<IDocumentService>(provider).SaveMyFolderAsync(
            null, new MyFolderRequest { Name = "Certificates" })).Folder!.Id;

        Guid id = await UploadMineAsync(provider, folderId: folderId);

        Assert.Equal(
            DocumentOutcome.FolderNotEmpty,
            await Resolve<IDocumentService>(provider).DeleteMyFolderAsync(folderId));

        // Moved out, and then it goes.
        Assert.True((await Resolve<IDocumentService>(provider)
            .MoveMineAsync(id, null)).Succeeded);

        Assert.Equal(
            DocumentOutcome.Succeeded,
            await Resolve<IDocumentService>(provider).DeleteMyFolderAsync(folderId));
    }

    [SkippableFact]
    public async Task AnUploadWithNoFolderGoesToTheirDefaultDrawer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await UploadMineAsync(provider);

        // Created on demand. FolderType.Personal existed for exactly this and nothing
        // could reach it before.
        FolderDto drawer = Assert.Single(
            await Resolve<IDocumentService>(provider).MyFoldersAsync());

        Assert.Equal("My documents", drawer.Name);
        Assert.Equal(1, drawer.DocumentCount);
    }

    [SkippableFact]
    public async Task TwoDrawersCannotShareAName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await Resolve<IDocumentService>(provider).SaveMyFolderAsync(
            null, new MyFolderRequest { Name = "Certificates" })).Succeeded);

        Assert.Equal(
            DocumentOutcome.DuplicateFolderName,
            (await Resolve<IDocumentService>(provider).SaveMyFolderAsync(
                null, new MyFolderRequest { Name = "Certificates" })).Outcome);
    }

    // The owner permanently has no employee record — they run the company's account
    // rather than working there, so they have no drawer of their own.
    [SkippableFact]
    public async Task AnAccountWithNoEmployeeRecordHasNoDrawer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ownerUserId;

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            ApplicationUser owner = await seed.Users.FirstAsync(
                user => user.TenantId == _tenantId && user.EmployeeId == null);

            ownerUserId = owner.Id;
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            ownerUserId.ToString();

        Assert.Empty(await Resolve<IDocumentService>(provider).MyFoldersAsync());

        Assert.Equal(
            DocumentOutcome.NoEmployeeRecord,
            (await Resolve<IDocumentService>(provider).SaveMyFolderAsync(
                null, new MyFolderRequest { Name = "Mine" })).Outcome);
    }
}
