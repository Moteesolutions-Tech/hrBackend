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

    // Deliberately not narrowed by a viewer's data scope, unlike the other registers.
    //
    // A document is not an employee's record — HrDocument has no EmployeeId. It is a
    // company artefact aimed at everybody (the handbook), at named departments, or at
    // named people through shares. So "which employees does this scope reach" is the
    // wrong question, and the right one — which departments is it assigned to — cannot
    // be asked in SQL: AssignedDepartmentIds is jsonb behind a value converter, so
    // narrowing by it would mean loading the whole library and filtering in memory,
    // which works until a company has a lot of documents.
    //
    // The organisation-wide gate on the endpoint keeps ordinary employees out, and
    // their own documents come through the /mine routes. Narrowing the library itself
    // for a department-scoped HR user is a product decision and a schema change, not
    // something to infer here.
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

    // ── self-service ────────────────────────────────────────────────────────
    //
    // Everything below acts as the signed-in employee, on their own drawer. The rule
    // running through all of it is that /documents/mine returns two different things —
    // what HR shared or assigned, and what the employee put there — and only the second
    // is theirs to move, bin or re-file. Without that distinction somebody could bin the
    // staff handbook off their own file, or move their contract where HR cannot find it.

    // Uploading something for HR to look at: a certificate, a licence renewal.
    //
    // Lands awaiting review rather than filed. A document waiting to be accepted is not
    // part of the record — otherwise an employee could put anything on their own file
    // by uploading it, which is the whole reason the queue exists.
    Task<DocumentResult> UploadMineAsync(
        SelfUploadRequest request,
        CancellationToken cancellationToken = default);

    // Their own drawers. Personal folders, owned by them — not the company's filing,
    // which they see through the shared documents they have been given.
    Task<IReadOnlyList<FolderDto>> MyFoldersAsync(CancellationToken cancellationToken = default);

    Task<FolderResult> SaveMyFolderAsync(
        Guid? id,
        MyFolderRequest request,
        CancellationToken cancellationToken = default);

    Task<DocumentOutcome> DeleteMyFolderAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // Null folder means out of any drawer, back to the top of their own list.
    Task<DocumentResult> MoveMineAsync(
        Guid id,
        Guid? folderId,
        CancellationToken cancellationToken = default);

    Task<DocumentResult> TrashMineAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DocumentResult> RestoreMineAsync(Guid id, CancellationToken cancellationToken = default);

    // ── HR's review queue ───────────────────────────────────────────────────

    // Self-service uploads waiting to be looked at, oldest first: a queue is worked
    // from the end that has been waiting longest.
    Task<PagedResult<DocumentDto>> ReviewsAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default);

    // Accepts the upload onto the employee's record, which is what makes it part of
    // the library rather than a pending request.
    Task<DocumentResult> AcceptUploadAsync(
        Guid id,
        string? note = null,
        CancellationToken cancellationToken = default);

    // Refused, with a reason, and still visible to whoever uploaded it. Hiding it would
    // leave them unable to tell a rejection from an upload that never arrived.
    Task<DocumentResult> RejectUploadAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default);

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

    // The swatch the screen draws. Null for a company folder that has never been
    // given one, which the client colours however it likes.
    public string? Colour { get; init; }
}

public sealed record MyFolderRequest
{
    public required string Name { get; init; }

    public string? Colour { get; init; }
}

public sealed record SelfUploadRequest
{
    public required string Name { get; init; }

    // Already uploaded through the files module with purpose EmployeeDocument. The
    // bytes have had their size and type checked there, and a document create should
    // not also be a multipart body.
    public required Guid FileId { get; init; }

    public DocumentCategory Category { get; init; } = DocumentCategory.Other;

    // A certificate or a licence that lapses. Carried so the expiry sweep picks it up
    // once HR accepts it, which is the point of telling anybody about it.
    public DateOnly? ExpiryDate { get; init; }

    public string? Description { get; init; }

    // One of their own drawers, or null for the top of their list.
    public Guid? FolderId { get; init; }
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

    // Set when an employee uploaded this themselves. Null for anything HR filed, which
    // is how the screen tells "your certificate" from "the staff handbook".
    public Guid? UploadedByEmployeeId { get; init; }

    public string? UploadedByName { get; init; }

    // Where a self-service upload has got to, and why if it was refused. Null for
    // anything HR filed — a document the company put there was never waiting on a
    // decision, and a status of "accepted" would imply somebody made one.
    public DocumentReviewStatus? ReviewStatus { get; init; }

    public string? ReviewNote { get; init; }

    // Whether the caller may move, bin or re-file this one themselves.
    //
    // Served rather than inferred, because the rule is not obvious from the row: the
    // self-service list carries both what HR assigned and what the employee uploaded,
    // and only the second is theirs to touch. A client working it out from
    // uploadedByEmployeeId would get it right today and wrong the first time the rule
    // gained a condition.
    public required bool IsMine { get; init; }

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

    // The signed-in account has no employee record, so it has no drawer of its own.
    // The owner is permanently in this state.
    NoEmployeeRecord,

    // Somebody else's. Covers both a document HR assigned — which is theirs to read
    // and not to bin — and a folder belonging to another employee. The same answer to
    // each, because telling them apart would say whose it is.
    NotYours,

    // Already accepted or refused. A second decision would record two people ruling
    // on one upload.
    AlreadyReviewed,

    // A decision asked for on something that was never a self-service upload. HR
    // filing a document is not a request for permission.
    NotAwaitingReview,

    // A rejection with nothing said. "Your document was rejected" and no reason sends
    // somebody to HR to ask.
    ReasonRequired,

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
