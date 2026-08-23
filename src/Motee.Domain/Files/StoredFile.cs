using Motee.Domain.Common;

namespace Motee.Domain.Files;

public class StoredFile : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required FilePurpose Purpose { get; set; }

    // What the file belongs to — an employee for an avatar or document, null for
    // tenant-wide things like the logo. Deliberately untyped: a foreign key per
    // purpose would mean a new column for every module that stores a file.
    public Guid? OwnerId { get; set; }

    // What the uploader called it. Shown back to them; never used to build the key.
    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    // Where it actually lives. Generated, so a hostile filename cannot influence the
    // storage path.
    public required string StorageKey { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
