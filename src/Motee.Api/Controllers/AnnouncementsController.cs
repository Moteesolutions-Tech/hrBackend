using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Announcements;
using Motee.Application.Common;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// The company noticeboard.
//
// Split by who is doing what, like contracts: /feed and the two routes under it
// are the employee's own view, resolved against them rather than taking an id on
// trust, while the rest is writing and managing notices.
//
// Nothing here flips a status on a schedule. A notice dated for Monday appears
// on Monday because the date has passed, so there is no publish job to be down
// and no window where the all-hands notice is invisible for the wrong reason.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/announcements")]
public class AnnouncementsController(IAnnouncementService announcements) : ApiControllerBase
{
    private const string Module = "workspace.announcements";

    // ── the employee's side ─────────────────────────────────────────────────

    // Everything live and addressed to them, pinned first. No permission beyond
    // being staff: a noticeboard nobody can read is not a noticeboard.
    [HttpGet("feed")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<AnnouncementDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Feed(CancellationToken cancellationToken) =>
        Ok(await announcements.FeedAsync(cancellationToken));

    [HttpPost("feed/{id:guid}/view")]
    [RequiresEmployee]
    [ProducesResponseType<AnnouncementDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkViewed(Guid id, CancellationToken cancellationToken) =>
        Respond(await announcements.MarkViewedAsync(id, cancellationToken), "Marked as read.");

    [HttpPost("feed/{id:guid}/acknowledge")]
    [RequiresEmployee]
    [ProducesResponseType<AnnouncementDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken cancellationToken) =>
        Respond(await announcements.AcknowledgeAsync(id, cancellationToken), "Acknowledged.");

    // ── writing and managing ────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType<PagedResult<AnnouncementDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] AnnouncementQuery query,
        CancellationToken cancellationToken) =>
        Ok(await announcements.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AnnouncementDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        AnnouncementDto? announcement = await announcements.GetAsync(id, cancellationToken);

        return announcement is null
            ? Failure<AnnouncementDto>(MoteeStatusCodes.NotFound, "Announcement not found.")
            : Ok(announcement);
    }

    // Who has agreed and who has not, by name. The outstanding list is the point
    // of the screen — a count is not something anybody can chase.
    [HttpGet("{id:guid}/acknowledgements")]
    [ProducesResponseType<AcknowledgementSummaryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Acknowledgements(
        Guid id,
        CancellationToken cancellationToken)
    {
        AcknowledgementSummaryDto? summary =
            await announcements.AcknowledgementsAsync(id, cancellationToken);

        return summary is null
            ? Failure<AcknowledgementSummaryDto>(
                MoteeStatusCodes.NotFound, "Announcement not found.")
            : Ok(summary);
    }

    // Creates a draft, or a scheduled notice if the request carries a date.
    // There is no separate schedule route: a date and a button would be two ways
    // to say one thing, and they would eventually disagree.
    [HttpPost]
    [ProducesResponseType<AnnouncementDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        AnnouncementRequest request,
        CancellationToken cancellationToken)
    {
        AnnouncementResult result = await announcements.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Announcement!, "Announcement saved.")
            : Failure<AnnouncementDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Refused once anybody has acknowledged it. Their agreement was to what it
    // said then, and the service refuses rather than trusting no screen offers
    // the edit.
    [HttpPut("{id:guid}")]
    [ProducesResponseType<AnnouncementDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        AnnouncementRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await announcements.UpdateAsync(id, request, cancellationToken),
            "Announcement updated.");

    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType<AnnouncementDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken) =>
        Respond(await announcements.PublishAsync(id, cancellationToken), "Published.");

    // Takes it off the board without destroying what people agreed to. Deleting
    // is for something posted by mistake and never seen.
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType<AnnouncementDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken) =>
        Respond(await announcements.ArchiveAsync(id, cancellationToken), "Archived.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        AnnouncementOutcome outcome = await announcements.DeleteAsync(id, cancellationToken);

        return outcome == AnnouncementOutcome.Succeeded
            ? Ok<object?>(null, "Announcement deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private IActionResult Respond(AnnouncementResult result, string message) =>
        result.Succeeded
            ? Ok(result.Announcement!, message)
            : Failure<AnnouncementDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));

    private static string StatusFor(AnnouncementOutcome outcome) => outcome switch
    {
        AnnouncementOutcome.NotFound or AnnouncementOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,

        // Not found rather than forbidden. Telling somebody a notice exists but
        // is not for them is itself a leak — a department reorganisation would be
        // readable from the error messages alone.
        AnnouncementOutcome.NotAvailable => MoteeStatusCodes.NotFound,

        AnnouncementOutcome.AlreadyAcknowledged
            or AnnouncementOutcome.AlreadyAcknowledgedByYou => MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(AnnouncementOutcome outcome) => outcome switch
    {
        AnnouncementOutcome.NotFound => "Announcement not found.",
        AnnouncementOutcome.NotAvailable => "Announcement not found.",
        AnnouncementOutcome.EmployeeNotFound => "That person does not work here.",
        AnnouncementOutcome.TitleRequired => "Give the announcement a title.",
        AnnouncementOutcome.BodyRequired => "Write something before posting it.",
        AnnouncementOutcome.DepartmentsRequired =>
            "Choose at least one department, or send it to all staff.",
        AnnouncementOutcome.ExpiresBeforeItAppears =>
            "This would expire before it appears. Move the expiry after the "
            + "scheduled date.",
        AnnouncementOutcome.AlreadyAcknowledged =>
            "People have already acknowledged this, so its wording cannot change. "
            + "Post a follow-up instead.",
        AnnouncementOutcome.AcknowledgementNotRequired =>
            "This announcement does not ask for an acknowledgement.",
        AnnouncementOutcome.AlreadyAcknowledgedByYou => "You have already acknowledged this.",
        _ => "Could not save the announcement.",
    };
}
