using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Exports;
using Motee.Application.Files;
using Motee.Application.Tenancy;
using Motee.Domain.Files;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Files;

internal sealed class FileUploadService(
    MoteeDbContext dbContext,
    IFileStorage storage,
    IRequestContext requestContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IFileUploadService
{
    // Enough to identify every format the policy checks.
    private const int SignatureBytes = 16;

    public async Task<FileUploadResult> UploadAsync(
        FileUpload upload,
        CancellationToken cancellationToken = default)
    {
        FileRejection rejection = FilePolicy.Check(upload.Purpose, upload.ContentType, upload.SizeBytes);

        if (rejection != FileRejection.None)
        {
            return FileUploadResult.Failed(rejection);
        }

        // Read the head before anything is written, so bytes that contradict the
        // declared type never reach storage.
        (byte[] head, Stream content) = await PeekAsync(upload.Content, cancellationToken);

        if (!FilePolicy.MatchesSignature(upload.ContentType, head))
        {
            return FileUploadResult.Failed(FileRejection.ContentDoesNotMatchType);
        }

        Guid id = Guid.NewGuid();

        // The key needs the tenant before the row is saved, so it is read here rather
        // than waiting for the context to stamp it.
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return FileUploadResult.Failed(FileRejection.NoTenant);
        }

        // The stored name is generated. A filename like ../../etc/passwd, or one
        // ending .html that a browser would render, cannot influence the path.
        string extension = SafeExtension(upload.FileName);
        string key = $"{tenantId:N}/{Folder(upload.Purpose)}/{id:N}{extension}";

        await storage.SaveAsync(key, content, upload.ContentType, cancellationToken);

        StoredFile file = new()
        {
            Id = id,
            Purpose = upload.Purpose,
            OwnerId = upload.OwnerId,
            FileName = Path.GetFileName(upload.FileName),
            ContentType = upload.ContentType,
            SizeBytes = upload.SizeBytes,
            StorageKey = key,
            UploadedByUserId = Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null,
            UploadedAt = timeProvider.GetUtcNow(),
        };

        dbContext.StoredFiles.Add(file);
        await dbContext.SaveChangesAsync(cancellationToken);

        return FileUploadResult.Ok(ToDto(file));
    }

    public async Task<StoredFileDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        StoredFile? file = await dbContext.StoredFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return file is null ? null : ToDto(file);
    }

    public async Task<IReadOnlyList<StoredFileDto>> ListAsync(
        FilePurpose purpose,
        Guid? ownerId,
        CancellationToken cancellationToken = default) =>
        await dbContext.StoredFiles
            .AsNoTracking()
            .Where(file => file.Purpose == purpose && (ownerId == null || file.OwnerId == ownerId))
            .OrderByDescending(file => file.UploadedAt)
            .Select(file => new StoredFileDto
            {
                Id = file.Id,
                Purpose = file.Purpose,
                OwnerId = file.OwnerId,
                FileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
                UploadedAt = file.UploadedAt,
                UploadedByUserId = file.UploadedByUserId,
            })
            .ToListAsync(cancellationToken);

    public async Task<FileContent?> OpenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Tenant-filtered, so another company's file id is simply not here.
        StoredFile? file = await dbContext.StoredFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (file is null)
        {
            return null;
        }

        Stream? content = await storage.OpenReadAsync(file.StorageKey, cancellationToken);

        return content is null
            ? null
            : new FileContent
            {
                Content = content,
                FileName = file.FileName,
                ContentType = file.ContentType,
            };
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        StoredFile? file = await dbContext.StoredFiles
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (file is null)
        {
            return false;
        }

        // The row goes first. An orphaned object costs storage; a row pointing at a
        // deleted object is a broken download.
        dbContext.StoredFiles.Remove(file);
        await dbContext.SaveChangesAsync(cancellationToken);

        await storage.DeleteAsync(file.StorageKey, cancellationToken);

        return true;
    }

    private static async Task<(byte[] Head, Stream Content)> PeekAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        // Buffered so the head can be read and the whole stream still uploaded, even
        // when the source cannot seek.
        MemoryStream buffered = new();
        await content.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;

        byte[] head = new byte[Math.Min(SignatureBytes, buffered.Length)];
        _ = await buffered.ReadAsync(head, cancellationToken);
        buffered.Position = 0;

        return (head, buffered);
    }

    private static string Folder(FilePurpose purpose) => purpose switch
    {
        FilePurpose.CompanyLogo => "logos",
        FilePurpose.EmployeeAvatar => "avatars",
        FilePurpose.EmployeeDocument => "documents",
        FilePurpose.Export => "exports",
        _ => "other",
    };

    // Only a short alphanumeric extension survives, so nothing in the caller's name
    // reaches the key.
    private static string SafeExtension(string fileName)
    {
        string extension = Path.GetExtension(fileName);

        return extension.Length is > 1 and <= 10 && extension[1..].All(char.IsLetterOrDigit)
            ? extension.ToLowerInvariant()
            : string.Empty;
    }

    private static StoredFileDto ToDto(StoredFile file) => new()
    {
        Id = file.Id,
        Purpose = file.Purpose,
        OwnerId = file.OwnerId,
        FileName = file.FileName,
        ContentType = file.ContentType,
        SizeBytes = file.SizeBytes,
        UploadedAt = file.UploadedAt,
        UploadedByUserId = file.UploadedByUserId,
    };
}
