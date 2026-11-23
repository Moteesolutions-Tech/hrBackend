using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Files;
using Motee.Application.Projects;
using Motee.Domain.Files;
using Motee.Domain.Projects;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Projects;

// Rollout documents hung off a project.
//
// The bytes go through the one upload path, so the size ceiling and the type
// check are the product's rather than this module's. What is stored here is
// which project the file belongs to and what kind of document it is.
internal sealed class ProjectDocumentService(
    MoteeDbContext dbContext,
    IFileUploadService files,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IProjectDocumentService
{
    public async Task<IReadOnlyList<ProjectDocumentDto>> ListAsync(
        Guid projectId,
        ProjectDocumentCategory? category = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ProjectDocument> documents = dbContext.ProjectDocuments
            .AsNoTracking()
            .Where(document => document.ProjectId == projectId);

        if (category is ProjectDocumentCategory only)
        {
            documents = documents.Where(document => document.Category == only);
        }

        List<ProjectDocument> rows = await documents
            .OrderByDescending(document => document.UploadedAt)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(rows, cancellationToken);
    }

    public async Task<ProjectDocumentResult> AttachAsync(
        Guid projectId,
        ProjectDocumentUpload upload,
        CancellationToken cancellationToken = default)
    {
        Project? project = await dbContext.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == projectId, cancellationToken);

        if (project is null)
        {
            return ProjectDocumentResult.Failed(ProjectOutcome.NotFound);
        }

        FileUploadResult stored = await files.UploadAsync(
            new FileUpload
            {
                Purpose = FilePurpose.ProjectDocument,
                OwnerId = projectId,
                FileName = upload.FileName,
                ContentType = upload.ContentType,
                Content = upload.Content,
                SizeBytes = upload.SizeBytes,
            },
            cancellationToken);

        if (!stored.Succeeded)
        {
            return ProjectDocumentResult.Rejected(stored.Rejection);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ProjectDocument document = new()
        {
            Id = Guid.NewGuid(),
            TenantId = project.TenantId,
            ProjectId = projectId,

            // Falls back to the file's own name: a document with no title in
            // the list is unfindable.
            Name = string.IsNullOrWhiteSpace(upload.Name)
                ? upload.FileName
                : upload.Name.Trim(),
            Category = upload.Category,
            Description = string.IsNullOrWhiteSpace(upload.Description)
                ? null
                : upload.Description.Trim(),
            StoredFileId = stored.File!.Id,
            UploadedByUserId = UserId(),
            UploadedAt = now,
        };

        dbContext.ProjectDocuments.Add(document);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectDocumentResult.Ok(
            (await DescribeAsync([document], cancellationToken))[0]);
    }

    public async Task<FileContent?> OpenAsync(
        Guid projectId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        ProjectDocument? document = await dbContext.ProjectDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Id == documentId && row.ProjectId == projectId,
                cancellationToken);

        return document is null
            ? null
            : await files.OpenAsync(document.StoredFileId, cancellationToken);
    }

    public async Task<ProjectDocumentResult> DeleteAsync(
        Guid projectId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        ProjectDocument? document = await dbContext.ProjectDocuments
            .FirstOrDefaultAsync(
                row => row.Id == documentId && row.ProjectId == projectId,
                cancellationToken);

        if (document is null)
        {
            return ProjectDocumentResult.Failed(ProjectOutcome.NotFound);
        }

        dbContext.ProjectDocuments.Remove(document);

        await dbContext.SaveChangesAsync(cancellationToken);

        // The row goes first. A file left in storage with nothing pointing at
        // it is waste; a row pointing at a file that is gone is a broken
        // download.
        await files.DeleteAsync(document.StoredFileId, cancellationToken);

        return ProjectDocumentResult.Done();
    }

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private async Task<List<ProjectDocumentDto>> DescribeAsync(
        IReadOnlyList<ProjectDocument> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return [];
        }

        List<Guid> fileIds = [.. documents.Select(document => document.StoredFileId)];

        Dictionary<Guid, StoredFile> stored = await dbContext.StoredFiles
            .AsNoTracking()
            .Where(file => fileIds.Contains(file.Id))
            .ToDictionaryAsync(file => file.Id, cancellationToken);

        return
        [
            .. documents.Select(document =>
            {
                StoredFile? file = stored.GetValueOrDefault(document.StoredFileId);

                return new ProjectDocumentDto
                {
                    Id = document.Id,
                    ProjectId = document.ProjectId,
                    Name = document.Name,
                    Category = document.Category,
                    Description = document.Description,
                    StoredFileId = document.StoredFileId,
                    FileName = file?.FileName ?? document.Name,
                    ContentType = file?.ContentType ?? "application/octet-stream",
                    SizeBytes = file?.SizeBytes ?? 0,
                    UploadedByUserId = document.UploadedByUserId,
                    UploadedAt = document.UploadedAt,
                };
            }),
        ];
    }
}
