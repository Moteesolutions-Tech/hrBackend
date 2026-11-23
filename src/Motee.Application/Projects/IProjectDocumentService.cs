using Motee.Application.Files;
using Motee.Domain.Files;
using Motee.Domain.Projects;

namespace Motee.Application.Projects;

// Rollout documents hung off a project.
//
// The bytes go through the one upload path, so the size limits and type checks
// are the product's, written once. What is stored here is only which project a
// file belongs to and what kind of document it is.
public interface IProjectDocumentService
{
    Task<IReadOnlyList<ProjectDocumentDto>> ListAsync(
        Guid projectId,
        ProjectDocumentCategory? category = null,
        CancellationToken cancellationToken = default);

    Task<ProjectDocumentResult> AttachAsync(
        Guid projectId,
        ProjectDocumentUpload upload,
        CancellationToken cancellationToken = default);

    Task<FileContent?> OpenAsync(
        Guid projectId,
        Guid documentId,
        CancellationToken cancellationToken = default);

    Task<ProjectDocumentResult> DeleteAsync(
        Guid projectId,
        Guid documentId,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectDocumentDto
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required string Name { get; init; }

    public required ProjectDocumentCategory Category { get; init; }

    public string? Description { get; init; }

    public required Guid StoredFileId { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required long SizeBytes { get; init; }

    public Guid? UploadedByUserId { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }
}

public sealed record ProjectDocumentUpload
{
    public string? Name { get; init; }

    public ProjectDocumentCategory Category { get; init; }

    public string? Description { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required Stream Content { get; init; }

    public required long SizeBytes { get; init; }
}

public sealed record ProjectDocumentResult
{
    public required ProjectOutcome Outcome { get; init; }

    public ProjectDocumentDto? Document { get; init; }

    // Why the file itself was turned away — too large, wrong type, empty.
    public FileRejection Rejection { get; init; }

    public bool Succeeded => Outcome == ProjectOutcome.Succeeded;

    public static ProjectDocumentResult Failed(ProjectOutcome outcome) =>
        new() { Outcome = outcome };

    public static ProjectDocumentResult Rejected(FileRejection rejection) =>
        new() { Outcome = ProjectOutcome.Refused, Rejection = rejection };

    public static ProjectDocumentResult Ok(ProjectDocumentDto document) =>
        new() { Outcome = ProjectOutcome.Succeeded, Document = document };

    public static ProjectDocumentResult Done() => new() { Outcome = ProjectOutcome.Succeeded };
}
