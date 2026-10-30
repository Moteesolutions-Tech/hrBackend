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
}
