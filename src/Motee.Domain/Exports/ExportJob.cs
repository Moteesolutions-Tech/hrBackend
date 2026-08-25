using Motee.Domain.Common;

namespace Motee.Domain.Exports;

public enum ExportStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

public class ExportJob : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Which module asked, e.g. "employees". Kept as a string so a new export does not
    // need this enum widened.
    public required string Kind { get; set; }

    public Guid? RequestedByUserId { get; set; }

    public required string RequestedByEmail { get; set; }

    // The filters the user had applied, rebuilt into a query when the job runs. The
    // job never carries SQL: a serialised query would outlive the permission check
    // that produced it.
    public required string Filters { get; set; }

    public ExportStatus Status { get; set; } = ExportStatus.Queued;

    public int RowCount { get; set; }

    // Storage key, not a URL. The download goes through the API so the file stays
    // behind the same permission check that produced it.
    public string? FileKey { get; set; }

    public string? FileName { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    // Personal data does not sit in a bucket for ever.
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsDownloadable(DateTimeOffset now) =>
        Status == ExportStatus.Completed
        && FileKey is not null
        && (ExpiresAt is null || ExpiresAt > now);
}

public static class ExportPolicy
{
    // Long enough to act on, short enough that a forgotten export of everyone's
    // personal details is not sitting in a bucket months later.
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    // How long the emailed link stays usable. Shorter than retention on purpose: the
    // link is unauthenticated, so anyone the email is forwarded to can open it, while
    // asking for a fresh link needs a login.
    //
    // One hour, not the seven days a signature can technically carry, because a
    // presigned URL cannot outlive the credentials that signed it. Running under an
    // instance or task role means temporary credentials that rotate every few hours -
    // so a link promising 24 hours dies quietly that afternoon with an opaque
    // AccessDenied, and the recipient has no way to know why. Better to promise an hour
    // and mean it.
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static DateTimeOffset ExpiresAt(DateTimeOffset completedAt) => completedAt.Add(Retention);
}
