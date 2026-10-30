using Motee.Domain.Common;

namespace Motee.Domain.Notifications;

// What kind of thing happened, in business terms.
//
// Distinct from severity, which is how it looks. The kind is what preferences,
// deduplication and "stop telling me about this" all hang off — and none of those
// can hang off an icon.
public enum NotificationKind
{
    ContractExpiring,
    DocumentExpiring,

    // A policy somebody has been given and not yet agreed to.
    AcknowledgementOutstanding,

    // A week that has finished and not been handed in.
    TimesheetDue,

    // A review past its date and unfinished.
    ReviewOverdue,

    // A goal past its date and still open.
    GoalOverdue,

    // Clocked in and never out. The day keeps accruing hours against the clock
    // until somebody corrects it.
    OpenClockSession,
}

// How it reads, which is the icon and colour the client picks.
public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
}

// One thing one person needs to know.
//
// Addressed to a user rather than an employee or a role. The bell belongs to
// whoever signs in: the owner has no employee record and holds no access level
// since the separation, yet absolutely needs to hear that a contract is lapsing —
// and somebody with no account cannot see a notification whatever it says.
//
// Resolving role to user happens when the notification is raised, not when it is
// read. That means who was told is a recorded fact rather than a query that
// quietly changes answer when somebody's access level is edited.
public class Notification : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public NotificationKind Kind { get; set; }

    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;

    public required string Title { get; set; }

    public required string Body { get; set; }

    // Where to go to deal with it. Without one a notification is an observation
    // rather than something somebody can act on.
    public string? Href { get; set; }

    // What it is about. The pair that makes a repeat sweep a no-op rather than a
    // second copy — "contract expiring" about contract X is one notification,
    // however many nights the job runs.
    public string? SubjectType { get; set; }

    public Guid? SubjectId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    // Put away deliberately. Distinct from read: read is "I have seen this",
    // dismissed is "stop showing me". A standing condition like an expiring
    // contract is still true after it is read, so without dismissal it would sit
    // in the panel until the contract ended.
    public DateTimeOffset? DismissedAt { get; set; }
}

public static class Notifications
{
    // Still worth showing. Read ones stay until dismissed, because a condition
    // that is still true should not disappear because somebody glanced at it.
    public static bool IsVisible(Notification notification) =>
        notification.DismissedAt is null;
}
