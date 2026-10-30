using Motee.Application.Common;
using Motee.Domain.Announcements;

namespace Motee.Application.Announcements;

// The company noticeboard.
//
// Scheduling needs no job: an announcement dated for Monday becomes visible on
// Monday because the date is the answer, not because something ran. The same goes
// for expiry. That keeps the feed correct even if every worker is down.
public interface IAnnouncementService
{
    Task<PagedResult<AnnouncementDto>> ListAsync(
        AnnouncementQuery query,
        CancellationToken cancellationToken = default);

    Task<AnnouncementDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AnnouncementResult> CreateAsync(
        AnnouncementRequest request,
        CancellationToken cancellationToken = default);

    Task<AnnouncementResult> UpdateAsync(
        Guid id,
        AnnouncementRequest request,
        CancellationToken cancellationToken = default);

    Task<AnnouncementResult> PublishAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AnnouncementResult> ArchiveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AnnouncementOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // Who has agreed, and who has not. Named rather than counted, because a
    // number is not something anybody can chase.
    Task<AcknowledgementSummaryDto?> AcknowledgementsAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // ── the employee's side ─────────────────────────────────────────────────

    // Everything live and addressed to them, pinned first.
    Task<IReadOnlyList<AnnouncementDto>> FeedAsync(CancellationToken cancellationToken = default);

    // Opening it. Idempotent, so the count means unique readers rather than
    // clicks and refreshing does not inflate it.
    Task<AnnouncementResult> MarkViewedAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<AnnouncementResult> AcknowledgeAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed record AnnouncementDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public required AnnouncementType Type { get; init; }

    // What the screen shows: draft, scheduled, published, expired or archived.
    // Partly derived, which is why it is a string rather than the stored enum —
    // a scheduled notice whose time has come reads as published.
    public required string Status { get; init; }

    public required AnnouncementPriority Priority { get; init; }

    public required AnnouncementAudience Audience { get; init; }

    public required IReadOnlyList<Guid> TargetDepartmentIds { get; init; }

    // The same departments by name. Sent alongside the ids rather than instead
    // of them: the form writes ids back, and the card reads names — an id is
    // not something anybody can put on a screen.
    public required IReadOnlyList<string> TargetDepartments { get; init; }

    public required bool IsPinned { get; init; }

    public required bool RequiresAcknowledgement { get; init; }

    public DateTimeOffset? ScheduledFor { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public Guid? AttachmentFileId { get; init; }

    public string? AttachmentName { get; init; }

    public string? CreatedByName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    // Unique readers, not clicks.
    public required int ViewCount { get; init; }

    public required int AcknowledgedCount { get; init; }

    // Who has agreed, by name and department. Carried on the announcement
    // rather than only behind its own call because the card shows a count and
    // the detail panel breaks it down by department — and the rows are already
    // loaded to produce the count, so withholding them would save nothing.
    public required IReadOnlyList<AcknowledgedPersonDto> Acknowledgements { get; init; }

    // Everybody it reaches, worked out from the audience.
    public required int TotalTargeted { get; init; }

    // Whether the caller has agreed. Null when none is asked for, which reads
    // differently from "not yet".
    public bool? AcknowledgedByMe { get; init; }
}

public sealed record AcknowledgementSummaryDto
{
    public required Guid AnnouncementId { get; init; }

    public required int TotalTargeted { get; init; }

    public required IReadOnlyList<AcknowledgedPersonDto> Acknowledged { get; init; }

    public required IReadOnlyList<string> Outstanding { get; init; }
}

public sealed record AcknowledgedPersonDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    // Where they sit, for the breakdown on the detail panel. Null for somebody
    // in no department, which is a real state here rather than bad data.
    public string? Department { get; init; }

    public required DateTimeOffset AcknowledgedAt { get; init; }
}

public sealed record AnnouncementRequest
{
    public required string Title { get; init; }

    public required string Body { get; init; }

    public required AnnouncementType Type { get; init; }

    public AnnouncementPriority Priority { get; init; } = AnnouncementPriority.Standard;

    public AnnouncementAudience Audience { get; init; } = AnnouncementAudience.AllStaff;

    public IReadOnlyList<Guid>? TargetDepartmentIds { get; init; }

    public bool IsPinned { get; init; }

    public bool RequiresAcknowledgement { get; init; }

    // Dating it in the future schedules it. There is no separate "schedule"
    // action, because a date and an action would be two ways to say one thing.
    public DateTimeOffset? ScheduledFor { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public Guid? AttachmentFileId { get; init; }
}

public sealed record AnnouncementQuery : PagedQuery
{
    public AnnouncementType? Type { get; init; }

    public AnnouncementAudience? Audience { get; init; }

    public bool IncludeArchived { get; init; }
}

public enum AnnouncementOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,

    TitleRequired,
    BodyRequired,

    // A department audience with no departments named reaches nobody, which is
    // not what anybody meant by choosing it.
    DepartmentsRequired,

    // Expiring before it appears. The notice would never be seen.
    ExpiresBeforeItAppears,

    // Editing something people have already agreed to. Their agreement was to
    // what it said then.
    AlreadyAcknowledged,

    AcknowledgementNotRequired,

    AlreadyAcknowledgedByYou,

    // Not visible to the caller — unpublished, expired, or addressed elsewhere.
    NotAvailable,
}

public sealed record AnnouncementResult
{
    public required AnnouncementOutcome Outcome { get; init; }

    public AnnouncementDto? Announcement { get; init; }

    public bool Succeeded => Outcome == AnnouncementOutcome.Succeeded;

    public static AnnouncementResult Failed(AnnouncementOutcome outcome) =>
        new() { Outcome = outcome };

    public static AnnouncementResult Ok(AnnouncementDto announcement) =>
        new() { Outcome = AnnouncementOutcome.Succeeded, Announcement = announcement };
}
