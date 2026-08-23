using Motee.Domain.Files;

namespace Motee.Application.Files;

// One upload path for the whole product — logos, avatars, employee documents. A
// module adds a FilePurpose rather than its own storage code, so the size limits,
// type checks and tenant scoping are written once.
public interface IFileUploadService
{
    Task<FileUploadResult> UploadAsync(FileUpload upload, CancellationToken cancellationToken = default);

    Task<StoredFileDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredFileDto>> ListAsync(
        FilePurpose purpose,
        Guid? ownerId,
        CancellationToken cancellationToken = default);

    Task<FileContent?> OpenAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record FileUpload
{
    public required FilePurpose Purpose { get; init; }

    // What the file belongs to — the employee for an avatar or document. Null for
    // tenant-wide things like the company logo.
    public Guid? OwnerId { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required Stream Content { get; init; }

    public required long SizeBytes { get; init; }
}

public sealed record FileUploadResult
{
    public required FileRejection Rejection { get; init; }

    public StoredFileDto? File { get; init; }

    public bool Succeeded => Rejection == FileRejection.None;

    public static FileUploadResult Failed(FileRejection rejection) => new() { Rejection = rejection };

    public static FileUploadResult Ok(StoredFileDto file) =>
        new() { Rejection = FileRejection.None, File = file };
}

public sealed record StoredFileDto
{
    public required Guid Id { get; init; }

    public required FilePurpose Purpose { get; init; }

    public Guid? OwnerId { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }

    public Guid? UploadedByUserId { get; init; }
}

public sealed record FileContent
{
    public required Stream Content { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }
}
