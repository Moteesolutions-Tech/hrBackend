using System.Text;
using Motee.Application.Files;
using Motee.Application.Projects;
using Motee.Domain.Files;
using Motee.Domain.Projects;

namespace Motee.Tests.Integration;

// Rollout documents hung off a project.
//
// The bytes go through the one upload path, so what is proved here is that the
// product's size and type rules apply, that the file's facts are not copied
// alongside the row, and that removing a document does not leave the bytes
// behind.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class ProjectDocumentServiceTests(PostgresFixture fixture)
{
    private const string Pdf = "application/pdf";

    [SkippableFact]
    public async Task AttachesADocument()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDocumentResult result = await scene.Documents.AttachAsync(
            projectId,
            Attaching(
                name: "Project charter",
                category: ProjectDocumentCategory.Charter));

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Rejection}");

        ProjectDocumentDto document = result.Document!;

        Assert.Equal("Project charter", document.Name);
        Assert.Equal(ProjectDocumentCategory.Charter, document.Category);
        Assert.Equal("charter.pdf", document.FileName);
        Assert.Equal(Pdf, document.ContentType);
        Assert.True(document.SizeBytes > 0);
    }

    // A document with no title in the list is unfindable.
    [SkippableFact]
    public async Task FallsBackToTheFilesOwnName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDocumentResult result = await scene.Documents.AttachAsync(
            projectId, Attaching(name: "   "));

        Assert.Equal("charter.pdf", result.Document!.Name);
    }

    [SkippableFact]
    public async Task TurnsAwayAFileTypeTheProductDoesNotAccept()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDocumentResult result = await scene.Documents.AttachAsync(
            projectId,
            Attaching(fileName: "plan.exe", contentType: "application/x-msdownload"));

        Assert.Equal(ProjectOutcome.Refused, result.Outcome);
        Assert.Equal(FileRejection.DisallowedType, result.Rejection);
    }

    [SkippableFact]
    public async Task TurnsAwayAnEmptyFile()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDocumentResult result = await scene.Documents.AttachAsync(
            projectId, Attaching(body: string.Empty));

        Assert.Equal(FileRejection.Empty, result.Rejection);
    }

    [SkippableFact]
    public async Task ListsTheNewestFirstAndNarrowsByCategory()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        await scene.Documents.AttachAsync(
            projectId,
            Attaching(name: "Charter", category: ProjectDocumentCategory.Charter));

        scene.Clock.Advance(TimeSpan.FromMinutes(5));

        await scene.Documents.AttachAsync(
            projectId,
            Attaching(name: "UAT plan", category: ProjectDocumentCategory.UatPlan));

        Assert.Equal(
            ["UAT plan", "Charter"],
            (await scene.Documents.ListAsync(projectId)).Select(row => row.Name));

        Assert.Equal(
            ["Charter"],
            (await scene.Documents.ListAsync(projectId, ProjectDocumentCategory.Charter))
                .Select(row => row.Name));
    }

    [SkippableFact]
    public async Task HandsBackTheFileToDownload()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDocumentResult attached = await scene.Documents.AttachAsync(
            projectId, Attaching(body: "The charter, in full."));

        FileContent? content = await scene.Documents.OpenAsync(
            projectId, attached.Document!.Id);

        Assert.NotNull(content);
        Assert.Equal("charter.pdf", content.FileName);

        using StreamReader reader = new(content.Content);

        Assert.Equal(PdfHead + "The charter, in full.", await reader.ReadToEndAsync());
    }

    [SkippableFact]
    public async Task ADocumentFromAnotherProjectIsNotDownloadable()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDto other = await scene.StartedAsync(
            ProjectScene.Planning(code: "PRJ-002", name: "Payroll migration"));

        ProjectDocumentResult attached = await scene.Documents.AttachAsync(
            projectId, Attaching());

        Assert.Null(
            await scene.Documents.OpenAsync(other.Project.Id, attached.Document!.Id));
    }

    // A file left in storage with nothing pointing at it is waste.
    [SkippableFact]
    public async Task RemovingADocumentTakesTheBytesWithIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDocumentResult attached = await scene.Documents.AttachAsync(
            projectId, Attaching());

        Guid storedFileId = attached.Document!.StoredFileId;

        Assert.True(
            (await scene.Documents.DeleteAsync(projectId, attached.Document.Id)).Succeeded);

        Assert.Empty(await scene.Documents.ListAsync(projectId));
        Assert.Null(await scene.Resolve<IFileUploadService>().GetAsync(storedFileId));
    }

    // The upload path reads the first bytes and refuses anything that
    // contradicts its declared type, so a document that claims to be a PDF has
    // to start like one.
    private const string PdfHead = "%PDF-1.7\n";

    private static ProjectDocumentUpload Attaching(
        string? name = "Project charter",
        ProjectDocumentCategory category = ProjectDocumentCategory.Charter,
        string fileName = "charter.pdf",
        string contentType = Pdf,
        string body = "The charter, in full.") => new()
        {
            Name = name,
            Category = category,
            Description = "Signed off at the steering group.",
            FileName = fileName,
            ContentType = contentType,
            Content = new MemoryStream(Encoding.UTF8.GetBytes(Bytes(contentType, body))),
            SizeBytes = Encoding.UTF8.GetByteCount(Bytes(contentType, body)),
        };

    private static string Bytes(string contentType, string body) =>
        body.Length > 0 && contentType == Pdf ? PdfHead + body : body;

    private async Task<(ProjectScene Scene, Guid ProjectId)> ArrangeAsync()
    {
        ProjectScene scene = await new ProjectScene(fixture).ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        return (scene, project.Project.Id);
    }
}
