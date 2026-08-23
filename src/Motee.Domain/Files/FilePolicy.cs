namespace Motee.Domain.Files;

// What the file is for. Decides what may be uploaded and how large — a company logo
// and a signed contract are not the same risk.
public enum FilePurpose
{
    CompanyLogo,
    EmployeeAvatar,
    EmployeeDocument,
    Export,
}

public enum FileRejection
{
    None,
    UnknownPurpose,
    Empty,
    TooLarge,
    DisallowedType,

    // The declared type and the actual bytes disagree — a renamed executable, or a
    // client that guessed at the content type.
    ContentDoesNotMatchType,

    // Nothing resolved a tenant, so there is no company to store the file against.
    // A misconfiguration or an unauthenticated call, not anything the caller sent.
    NoTenant,
}

public sealed record FileRule
{
    public required long MaxBytes { get; init; }

    public required IReadOnlyList<string> ContentTypes { get; init; }
}

public static class FilePolicy
{
    private const long Kb = 1024;
    private const long Mb = 1024 * Kb;

    private static readonly string[] Images = ["image/png", "image/jpeg", "image/webp"];

    private static readonly Dictionary<FilePurpose, FileRule> Rules = new()
    {
        [FilePurpose.CompanyLogo] = new() { MaxBytes = 2 * Mb, ContentTypes = Images },
        [FilePurpose.EmployeeAvatar] = new() { MaxBytes = 2 * Mb, ContentTypes = Images },

        [FilePurpose.EmployeeDocument] = new()
        {
            MaxBytes = 20 * Mb,
            ContentTypes =
            [
                "application/pdf",
                "application/msword",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                "application/vnd.ms-excel",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                .. Images,
            ],
        },

        // Written by the export job, never uploaded by a caller.
        [FilePurpose.Export] = new() { MaxBytes = 200 * Mb, ContentTypes = ["text/csv"] },
    };

    public static FileRule? RuleFor(FilePurpose purpose) => Rules.GetValueOrDefault(purpose);

    public static FileRejection Check(FilePurpose purpose, string? contentType, long sizeBytes)
    {
        if (!Rules.TryGetValue(purpose, out FileRule? rule))
        {
            return FileRejection.UnknownPurpose;
        }

        if (sizeBytes <= 0)
        {
            return FileRejection.Empty;
        }

        if (sizeBytes > rule.MaxBytes)
        {
            return FileRejection.TooLarge;
        }

        string declared = Normalise(contentType);

        return rule.ContentTypes.Contains(declared, StringComparer.OrdinalIgnoreCase)
            ? FileRejection.None
            : FileRejection.DisallowedType;
    }

    // A client can claim any content type. For the formats with a stable signature
    // the first bytes are checked too, so a renamed executable is refused rather
    // than stored and served back later as an image.
    public static bool MatchesSignature(string? contentType, ReadOnlySpan<byte> head)
    {
        return Normalise(contentType) switch
        {
            "image/png" => Starts(head, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "image/jpeg" => Starts(head, [0xFF, 0xD8, 0xFF]),
            "image/webp" => head.Length >= 12
                && Starts(head, [0x52, 0x49, 0x46, 0x46])
                && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50,
            "application/pdf" => Starts(head, [0x25, 0x50, 0x44, 0x46]),

            // Office formats are zip containers, and the older ones are OLE2.
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" =>
                Starts(head, [0x50, 0x4B, 0x03, 0x04]),
            "application/msword" or "application/vnd.ms-excel" =>
                Starts(head, [0xD0, 0xCF, 0x11, 0xE0]),

            // Text has no signature; size and type limits are the only guard.
            _ => true,
        };
    }

    private static bool Starts(ReadOnlySpan<byte> head, ReadOnlySpan<byte> signature) =>
        head.Length >= signature.Length && head[..signature.Length].SequenceEqual(signature);

    private static string Normalise(string? contentType) =>
        contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
}
