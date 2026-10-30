using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Contracts;
using Motee.Application.Notifications;

namespace Motee.Api.Controllers;

// The bell.
//
// Authorize rather than RequiresPermission or RequiresEmployee: notifications are
// addressed to a user, and the owner — who is no employee and holds no access
// level — is exactly who needs to hear that a contract is lapsing. Every route
// here resolves against the caller, so there is nothing to grant.
[ApiController]
[ApiVersion("1.0")]
[Authorize]
[Route("api/v{version:apiVersion}/notifications")]
public class NotificationsController(INotificationService notifications) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<NotificationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(
        [FromQuery] bool unreadOnly,
        CancellationToken cancellationToken) =>
        Ok(await notifications.MineAsync(unreadOnly, cancellationToken));

    // The badge. Its own route because it is polled far more often than the list
    // and returning a page of bodies to render a number is waste on every poll.
    [HttpGet("unread-count")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UnreadCount(CancellationToken cancellationToken) =>
        Ok(await notifications.UnreadCountAsync(cancellationToken));

    [HttpPost("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken) =>
        await notifications.MarkReadAsync(id, cancellationToken)
            ? Ok<object?>(null, "Marked as read.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Notification not found.");

    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken) =>
        Ok(await notifications.MarkAllReadAsync(cancellationToken), "All marked as read.");

    // Put away for good, which read is not. A standing condition stays true after
    // somebody has glanced at it.
    [HttpPost("{id:guid}/dismiss")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken cancellationToken) =>
        await notifications.DismissAsync(id, cancellationToken)
            ? Ok<object?>(null, "Dismissed.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Notification not found.");
}
