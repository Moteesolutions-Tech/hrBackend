using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Notifications;
using Motee.Domain.Notifications;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Notifications;

internal sealed class NotificationService(
    MoteeDbContext dbContext,
    IRequestContext requestContext,
    TimeProvider timeProvider) : INotificationService
{
    public async Task<IReadOnlyList<NotificationDto>> MineAsync(
        bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (UserId() is not Guid userId)
        {
            return [];
        }

        IQueryable<Notification> source = dbContext.Notifications
            .AsNoTracking()
            .Where(notification => notification.UserId == userId
                && notification.DismissedAt == null);

        if (unreadOnly)
        {
            source = source.Where(notification => notification.ReadAt == null);
        }

        List<Notification> mine = await source
            .OrderByDescending(notification => notification.CreatedAt)
            .ToListAsync(cancellationToken);

        return
        [
            .. mine.Select(notification => new NotificationDto
            {
                Id = notification.Id,
                Kind = notification.Kind,
                Severity = notification.Severity,
                Title = notification.Title,
                Body = notification.Body,
                Href = notification.Href,
                SubjectType = notification.SubjectType,
                SubjectId = notification.SubjectId,
                Read = notification.ReadAt is not null,
                CreatedAt = notification.CreatedAt,
            }),
        ];
    }

    public async Task<int> UnreadCountAsync(CancellationToken cancellationToken = default)
    {
        if (UserId() is not Guid userId)
        {
            return 0;
        }

        return await dbContext.Notifications.CountAsync(
            notification => notification.UserId == userId
                && notification.ReadAt == null
                && notification.DismissedAt == null,
            cancellationToken);
    }

    public Task<bool> MarkReadAsync(Guid id, CancellationToken cancellationToken = default) =>
        SetAsync(id, notification => notification.ReadAt ??= timeProvider.GetUtcNow(),
            cancellationToken);

    public Task<bool> DismissAsync(Guid id, CancellationToken cancellationToken = default) =>
        SetAsync(
            id,
            notification =>
            {
                DateTimeOffset now = timeProvider.GetUtcNow();

                // Dismissing implies having seen it, and a dismissed row that
                // still counts as unread would leave a badge nobody can clear.
                notification.ReadAt ??= now;
                notification.DismissedAt = now;
            },
            cancellationToken);

    public async Task<int> MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        if (UserId() is not Guid userId)
        {
            return 0;
        }

        return await dbContext.Notifications
            .Where(notification => notification.UserId == userId
                && notification.ReadAt == null
                && notification.DismissedAt == null)
            .ExecuteUpdateAsync(
                row => row.SetProperty(
                    notification => notification.ReadAt, timeProvider.GetUtcNow()),
                cancellationToken);
    }

    public async Task<bool> RaiseAsync(
        RaiseNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        // Checked against every notification of that kind about that subject for
        // that person, dismissed ones included. A sweep that re-raised something
        // somebody had put away would make dismissing meaningless for exactly the
        // standing conditions this exists to carry.
        bool exists = await dbContext.Notifications
            .IgnoreQueryFilters()
            .AnyAsync(
                notification => notification.TenantId == request.TenantId
                    && notification.UserId == request.UserId
                    && notification.Kind == request.Kind
                    && notification.SubjectId == request.SubjectId,
                cancellationToken);

        if (exists)
        {
            return false;
        }

        dbContext.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),

            // Set explicitly: this is raised by a background sweep, which belongs
            // to no tenant, so there is nothing for the context to stamp it from.
            TenantId = request.TenantId,
            UserId = request.UserId,
            Kind = request.Kind,
            Severity = request.Severity,
            Title = request.Title.Trim(),
            Body = request.Body.Trim(),
            Href = request.Href,
            SubjectType = request.SubjectType,
            SubjectId = request.SubjectId,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    // Scoped to the caller, so a valid id is not permission to clear somebody
    // else's panel.
    private async Task<bool> SetAsync(
        Guid id,
        Action<Notification> apply,
        CancellationToken cancellationToken)
    {
        if (UserId() is not Guid userId)
        {
            return false;
        }

        Notification? notification = await dbContext.Notifications
            .FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.UserId == userId,
                cancellationToken);

        if (notification is null)
        {
            return false;
        }

        apply(notification);
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;
}
