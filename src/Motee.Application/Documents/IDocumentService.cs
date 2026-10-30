using Motee.Application.Common;
using Motee.Domain.Documents;

namespace Motee.Application.Documents;

// The company's filing, over the top of stored_files.
//
// The bytes are already handled — upload, download and expiry of signed links all
// belong to IFileStorage and the files endpoints. What is here is everything a file
// alone cannot say: where it is filed, which version is current, who it is for, and
// who has agreed to it.
public interface IDocumentService
{
    // ── folders ─────────────────────────────────────────────────────────────

    Task<IReadOnlyList<FolderDto>> FoldersAsync(CancellationToken cancellationToken = default);

    Task<FolderResult> CreateFolderAsync(
        FolderRequest request,
        CancellationToken cancellationToken = default);

    Task<FolderResult> RenameFolderAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default);

    Task<DocumentOutcome> DeleteFolderAsync(Guid id, CancellationToken cancellationToken = default);

    // ── documents ───────────────────────────────────────────────────────────

    Task<PagedResult<DocumentDto>> ListAsync(
        DocumentQuery query,
        CancellationToken cancellationToken = default);

    Task<DocumentDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // The file is uploaded first, through the files endpoint, and its id handed
    // here. Keeping the two apart means a large upload that fails does not take a
    // half-made document with it, and the browser can show progress on the part
    // that takes time.
    Task<DocumentResult> CreateAsync(
        CreateDocumentRequest request,
        CancellationToken cancellationToken = default);

    Task<DocumentResult> UpdateAsync(
        Guid id,
        UpdateDocumentRequest request,
        CancellationToken cancellationToken = default);

    // A new upload of the same document. Becomes the current version; the old one
    // stays, because "what did the policy say in March" is what this exists for.
    Task<DocumentResult> AddVersionAsync(
        Guid id,
        AddVersionRequest request,
        CancellationToken cancellationToken = default);

    Task<DocumentResult> ArchiveAsync(Guid id, CancellationToken cancellationToken = default);

    // Recoverable. Deleting removes the bytes; this hides the row.
    Task<DocumentResult> TrashAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DocumentResult> RestoreAsync(Guid id, CancellationToken cancellationToken = default);

    // ── sharing ─────────────────────────────────────────────────────────────

    Task<DocumentResult> ShareAsync(
        Guid id,
        ShareDocumentRequest request,
        CancellationToken cancellationToken = default);

    Task<DocumentOutcome> UnshareAsync(
        Guid id,
        Guid employeeId,
        CancellationToken cancellationToken = default);

    // ── the employee's side ─────────────────────────────────────────────────

    // Everything the caller can see: shared with them by name, assigned to their
    // department, or global. One read rather than three, because the screen shows
    // one list and working out which of the three a document arrived by is not the
    // employee's problem.
    Task<IReadOnlyList<DocumentDto>> MineAsync(CancellationToken cancellationToken = default);

    // Saying they have read it. Against the current version, so a reupload asks
    // again rather than inheriting an agreement to something else.
    Task<DocumentResult> AcknowledgeAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // Who has agreed and who has not, for the documents that require it.
    Task<AcknowledgementReportDto?> AcknowledgementsAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed record FolderDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required FolderType Type { get; init; }

    public Guid? ParentId { get; init; }

    public required int DocumentCount { get; init; }

    // System and trash folders are renameable but never deletable. Served rather
    // than inferred, so the client does not reimplement the rule and get it wrong.
    public required bool CanDelete { get; init; }
}

public sealed record DocumentVersionDto
{
    public required Guid Id { get; init; }

    public required int Version { get; init; }

    public required Guid StoredFileId { get; init; }

    public required long SizeBytes { get; init; }

    public string? Notes { get; init; }

    public string? UploadedByName { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }
}

public sealed record DocumentShareDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required DocumentPermission Permission { get; init; }

    public required DateTimeOffset SharedAt { get; init; }
}

public sealed record DocumentDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required DocumentCategory Category { get; init; }

    public required Guid FolderId { get; init; }

    public string? FolderName { get; init; }

    public string? Description { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    // Past its date. Derived, so it becomes true at midnight and stops the day a
    // renewal is filed.
    public required bool IsExpired { get; init; }

    public required AssignmentScope Assignment { get; init; }

    public required IReadOnlyList<Guid> AssignedDepartmentIds { get; init; }

    public required bool RequiresAcknowledgement { get; init; }

    public required bool IsArchived { get; init; }

    public required bool IsTrashed { get; init; }

    // Newest first. The current one is the first.
    public required IReadOnlyList<DocumentVersionDto> Versions { get; init; }

    public required IReadOnlyList<DocumentShareDto> Shares { get; init; }

    // Whether the caller has agreed to the current version. Null when the document
    // asks for no acknowledgement, which is a different thing from "not yet".
    public bool? AcknowledgedByMe { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record AcknowledgementReportDto
{
    public required Guid DocumentId { get; init; }

    public required int CurrentVersion { get; init; }

    // Everybody the document is assigned to, worked out from its scope.
    public required int TotalAssigned { get; init; }

    public required IReadOnlyList<AcknowledgedByDto> Acknowledged { get; init; }

    // Named, not counted. "Fourteen outstanding" is a number; a list is something
    // somebody can chase.
    public required IReadOnlyList<string> Outstanding { get; init; }
}

public sealed record AcknowledgedByDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required DateTimeOffset AcknowledgedAt { get; init; }
}

public sealed record FolderRequest
{
    public required string Name { get; init; }

    public Guid? ParentId { get; init; }
}

public sealed record CreateDocumentRequest
{
    public required string Name { get; init; }

    public required DocumentCategory Category { get; init; }

    public required Guid FolderId { get; init; }

    // Already uploaded through the files endpoint. The document is the filing
    // around it, not the bytes.
    public required Guid StoredFileId { get; init; }

    public string? Description { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    public AssignmentScope Assignment { get; init; } = AssignmentScope.Specific;

    public IReadOnlyList<Guid>? AssignedDepartmentIds { get; init; }

    public bool RequiresAcknowledgement { get; init; }
}

public sealed record UpdateDocumentRequest
{
    public required string Name { get; init; }

    public required DocumentCategory Category { get; init; }

    public required Guid FolderId { get; init; }

    public string? Description { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    public AssignmentScope Assignment { get; init; } = AssignmentScope.Specific;

    public IReadOnlyList<Guid>? AssignedDepartmentIds { get; init; }

    public bool RequiresAcknowledgement { get; init; }
}

public sealed record AddVersionRequest
{
    public required Guid StoredFileId { get; init; }

    public string? Notes { get; init; }
}

public sealed record ShareDocumentRequest
{
    public required Guid EmployeeId { get; init; }

    public DocumentPermission Permission { get; init; } = DocumentPermission.ViewOnly;
}

public sealed record DocumentQuery : PagedQuery
{
    public Guid? FolderId { get; init; }

    public DocumentCategory? Category { get; init; }

    // Archived and trashed are both out of the way, and both hidden by default —
    // a filing cabinet that shows deleted things is a filing cabinet nobody trusts.
    public bool IncludeArchived { get; init; }

    public bool IncludeTrashed { get; init; }

    public bool? ExpiredOnly { get; init; }
}

public enum DocumentOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,
    FolderNotFound,

    // The uploaded file does not exist, or belongs to another company.
    FileNotFound,

    NameRequired,

    // Two folders with one name under the same parent is a filing system nobody can
    // navigate.
    DuplicateFolderName,

    // System and trash folders are renameable but never deletable: screens reach
    // them by purpose.
    FolderNotDeletable,

    // Emptying it first is the point — deleting a folder of documents by accident
    // is not recoverable the way trashing one document is.
    FolderNotEmpty,

    // Acknowledging something that asks for none.
    AcknowledgementNotRequired,

    // Already agreed to this version. Saying so twice would move the date and make
    // a late acknowledgement look prompt.
    AlreadyAcknowledged,

    // Shared with somebody who already has it. Change the permission instead.
    AlreadyShared,

    NotAllowed,
}

public sealed record DocumentResult
{
    public required DocumentOutcome Outcome { get; init; }

    public DocumentDto? Document { get; init; }

    public bool Succeeded => Outcome == DocumentOutcome.Succeeded;

    public static DocumentResult Failed(DocumentOutcome outcome) => new() { Outcome = outcome };

    public static DocumentResult Ok(DocumentDto document) =>
        new() { Outcome = DocumentOutcome.Succeeded, Document = document };
}

public sealed record FolderResult
{
    public required DocumentOutcome Outcome { get; init; }

    public FolderDto? Folder { get; init; }

    public bool Succeeded => Outcome == DocumentOutcome.Succeeded;

    public static FolderResult Failed(DocumentOutcome outcome) => new() { Outcome = outcome };

    public static FolderResult Ok(FolderDto folder) =>
        new() { Outcome = DocumentOutcome.Succeeded, Folder = folder };
}
