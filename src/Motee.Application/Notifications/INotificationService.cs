using Motee.Domain.Notifications;

namespace Motee.Application.Notifications;

// The bell.
//
// Written to by the nightly sweep and read by whoever signs in. The property that
// makes the whole thing work is idempotence: raising the same notification twice
// about the same subject does nothing the second time, so a job can run every
// night for ninety days and the person is told once.
//
// That is why these are notifications rather than emails. "This contract expires
// in sixty days" is true every day for sixty days — emailing it nightly is how an
// inbox rule gets written, while a row created once and dismissed once is exactly
// right.
public interface INotificationService
{
    Task<IReadOnlyList<NotificationDto>> MineAsync(
        bool unreadOnly = false,
        CancellationToken cancellationToken = default);

    Task<int> UnreadCountAsync(CancellationToken cancellationToken = default);

    Task<bool> MarkReadAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> MarkAllReadAsync(CancellationToken cancellationToken = default);

    // Put away for good. A standing condition stays true after it is read, so
    // without this an expiring contract would sit in the panel until it expired.
    Task<bool> DismissAsync(Guid id, CancellationToken cancellationToken = default);

    // Raised by jobs rather than by a request, which is why it takes the recipient
    // rather than reading the current one.
    //
    // Returns false when the same notification already exists, so a caller can
    // tell "told them" from "already had" — which is what decides whether to send
    // an email alongside it.
    Task<bool> RaiseAsync(
        RaiseNotificationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record NotificationDto
{
    public required Guid Id { get; init; }

    public required NotificationKind Kind { get; init; }

    public required NotificationSeverity Severity { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public string? Href { get; init; }

    public string? SubjectType { get; init; }

    public Guid? SubjectId { get; init; }

    public required bool Read { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record RaiseNotificationRequest
{
    public required Guid TenantId { get; init; }

    public required Guid UserId { get; init; }

    public required NotificationKind Kind { get; init; }

    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;

    public required string Title { get; init; }

    public required string Body { get; init; }

    public string? Href { get; init; }

    public string? SubjectType { get; init; }

    // With the kind and the recipient, this is the identity of the notification.
    // Raising the same three again is a no-op.
    public Guid? SubjectId { get; init; }
}
