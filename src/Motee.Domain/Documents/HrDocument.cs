using Motee.Domain.Common;

namespace Motee.Domain.Documents;

public enum DocumentCategory
{
    Policy,
    Contract,
    Handbook,
    Form,
    Certificate,
    Report,

    // Passports, right-to-work evidence, proof of address, driving licences. Its own
    // category because this is the bulk of what employees upload about themselves, and
    // filing it under Other loses the one thing that makes it findable — and makes a
    // retention rule impossible to write.
    Identity,

    Other,
}

// Where a folder sits in the company's filing, and what may be done to it.
public enum FolderType
{
    // Made by the product, not the company — "Policies", "Contracts". Renameable
    // but not deletable: screens link to them by purpose, and a company that
    // deletes Contracts finds the contracts tab empty with nothing to explain it.
    System,

    // Somebody's own drawer. Visible to them and to whoever administers documents,
    // which is the difference between private and secret.
    Personal,

    Archive,
    Custom,
    Shared,

    // Soft-deleted. A folder rather than a flag on the document, because "show me
    // what was deleted" is a question people ask and a flag scattered across a
    // table answers it slowly.
    Trash,
}

public enum DocumentPermission
{
    // They can read it in the browser and nothing else. The weaker of the two and
    // the sane default for anything a company would not want forwarded.
    ViewOnly,

    Download,
}

// Where a document somebody uploaded themselves has got to.
//
// Only ever set on a self-service upload. HR filing a document is not a request for
// permission, so those rows carry null rather than a status implying somebody signed
// them off.
public enum DocumentReviewStatus
{
    // With HR. Deliberately not in the main library yet: a document waiting to be
    // accepted is not part of the record, and listing it as though it were would mean
    // an employee could put anything on their own file by uploading it.
    AwaitingReview,

    Accepted,

    // Refused, with a reason, and still visible to the person who uploaded it. Hiding
    // it would leave them unable to tell a rejection from an upload that never arrived.
    Rejected,
}

// Who a document is for.
public enum AssignmentScope
{
    // Everybody. The staff handbook.
    Global,

    // Named departments.
    Department,

    // Named people, through shares. The default, and what an ordinary upload is.
    Specific,
}

// A document the company holds, as distinct from the bytes it is made of.
//
// The bytes live in stored_files with every other upload, so this layer adds what a
// file alone cannot say: which folder it is filed in, who it is for, which version
// is current, whether it expires, and who has agreed to it.
public class HrDocument : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public DocumentCategory Category { get; set; }

    public Guid FolderId { get; set; }

    public string? Description { get; set; }

    // When it stops being valid — an insurance certificate, a signed policy due for
    // renewal. Null for the ones that do not expire, which is most.
    public DateOnly? ExpiryDate { get; set; }

    public AssignmentScope Assignment { get; set; } = AssignmentScope.Specific;

    // Department ids when the scope is Department. Ids rather than names, because a
    // department that gets renamed must not quietly stop being assigned.
    public List<Guid> AssignedDepartmentIds { get; set; } = [];

    // Reading it is not enough; they have to say they have. For the documents where
    // a company needs to show somebody agreed — a code of conduct, a policy change.
    public bool RequiresAcknowledgement { get; set; }

    // Out of circulation but kept. Distinct from trashed: archived is deliberate
    // and permanent, trashed is a mistake somebody may want back.
    public bool IsArchived { get; set; }

    public DateTimeOffset? TrashedAt { get; set; }

    // Set when an employee uploaded this themselves, rather than HR filing it.
    //
    // Keyed on the employee and not the account, so somebody's own uploads stay theirs
    // through a new account — the same reason a change request checks the employee.
    // Null for anything HR filed, which is most of the library.
    public Guid? UploadedByEmployeeId { get; set; }

    // Where a self-service upload has got to. Null for anything HR filed, because a
    // document the company put there was never waiting on anybody's decision — and a
    // default of "approved" would quietly say somebody reviewed it.
    public DocumentReviewStatus? ReviewStatus { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    // Why it was refused. Required on a rejection: "your document was rejected" with
    // no reason sends somebody to HR to ask, which is the cost the field avoids.
    public string? ReviewNote { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One upload of a document. The current one is the highest number.
//
// A row per version rather than overwriting, because "what did the policy say in
// March" is the question a document system exists to answer — and an employee who
// acknowledged version 2 has not agreed to version 3.
public class DocumentVersion : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    // 1, 2, 3. Per document, not global.
    public int Version { get; set; }

    // The bytes, in stored_files with every other upload.
    public Guid StoredFileId { get; set; }

    public long SizeBytes { get; set; }

    public string? Notes { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}

// A document handed to one person.
public class DocumentShare : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    public Guid EmployeeId { get; set; }

    public DocumentPermission Permission { get; set; } = DocumentPermission.ViewOnly;

    public Guid? SharedByUserId { get; set; }

    public DateTimeOffset SharedAt { get; set; }
}

// Somebody saying they have read it.
//
// Against a version, not the document. An acknowledgement that survived a reupload
// would let a company claim their staff agreed to a policy none of them has seen.
public class DocumentAcknowledgement : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    public Guid VersionId { get; set; }

    public Guid EmployeeId { get; set; }

    public DateTimeOffset AcknowledgedAt { get; set; }
}

public class DocumentFolder : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public FolderType Type { get; set; } = FolderType.Custom;

    // Null at the top. A tree rather than a flat list because companies file by
    // department inside category, and forcing that into one level means names like
    // "Policies - Engineering".
    public Guid? ParentId { get; set; }

    // Whose drawer this is, for a Personal folder. Null for every company folder.
    //
    // On the employee rather than inferred from CreatedByUserId, which is what was
    // there: an account is not a person. HR creating a drawer on somebody's behalf, or
    // an employee returning on a new account, both break the inference and neither
    // breaks this.
    public Guid? OwnerEmployeeId { get; set; }

    // The swatch the screen draws. Presentation, which is why it is a free string
    // rather than an enum — the palette belongs to the client, and pinning it here
    // would mean a release every time somebody wanted a different green.
    public string? Colour { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class Documents
{
    // Past its date. Derived, like every other date-relative answer here — a stored
    // flag would need a job to flip it and would be wrong the day after renewal.
    public static bool IsExpired(HrDocument document, DateOnly today) =>
        document.ExpiryDate is DateOnly expiry && expiry < today;

    // Deleting removes the bytes; trashing hides the row. A document in the trash is
    // recoverable, which is the whole reason the state exists.
    public static bool IsTrashed(HrDocument document) => document.TrashedAt is not null;

    // Renameable, never deletable. Screens reach these by purpose, so a company that
    // deletes Contracts finds that tab empty with nothing to explain it.
    public static bool IsDeletable(DocumentFolder folder) =>
        folder.Type is not (FolderType.System or FolderType.Trash);

    // Whether somebody may bin, move or re-file this document themselves.
    //
    // Only their own uploads. The rule exists because /documents/mine returns two very
    // different things: what HR shared or assigned to them, and what they put there.
    // Without this an employee could bin the staff handbook off their own file, or move
    // their contract into a folder HR cannot find — neither of which is theirs to do.
    public static bool IsOwnedBy(HrDocument document, Guid employeeId) =>
        document.UploadedByEmployeeId == employeeId;

    // Whether this row belongs in the company library yet.
    //
    // A self-service upload awaiting review is not part of the record. Listing it as
    // though it were would mean an employee could put anything on their own file by
    // uploading it, which is the whole reason the review queue exists.
    public static bool IsFiled(HrDocument document) =>
        document.ReviewStatus is null or DocumentReviewStatus.Accepted;

    // Waiting on HR. The review queue's one filter.
    public static bool AwaitsReview(HrDocument document) =>
        document.ReviewStatus == DocumentReviewStatus.AwaitingReview;
}
