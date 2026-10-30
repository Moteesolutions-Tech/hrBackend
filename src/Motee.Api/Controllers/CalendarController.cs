using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Auth;
using Motee.Application.Calendar;
using Motee.Domain.Authorization;
using Motee.Domain.Calendar;
using Motee.Domain.Identity;

namespace Motee.Api.Controllers;

// The calendar, for HR at /hr-action-center/events and for everybody else at
// /employee/events — the same screen, which is why these routes are not under
// an HR permission.
//
// Authorize rather than RequiresPermission, like the bell: every read here
// resolves against the caller and each source applies its own visibility rule
// inside the service. A module check at the door would be the wrong shape —
// it would either lock employees out of their own week or hand whoever passed
// it everybody else's.
//
// The one real grant is workspace.calendar, resolved below rather than as an
// attribute: creating an event is one endpoint, and whether it may land on the
// company calendar or on a stranger's depends on what is in the body.
[ApiController]
[ApiVersion("1.0")]
[Authorize]
[Route("api/v{version:apiVersion}/calendar")]
public class CalendarController(
    ICalendarService calendar,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "workspace.calendar";

    // A year and a bit. Long enough for the year view, short enough that a
    // mistyped date cannot ask for a decade of birthdays across the company.
    private const int LongestWindowDays = 400;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CalendarItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Agenda(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] Guid[]? employeeIds,
        CancellationToken cancellationToken)
    {
        if (to < from)
        {
            return Failure<IReadOnlyList<CalendarItemDto>>(
                MoteeStatusCodes.InvalidRequest, "The window ends before it starts.");
        }

        if (to.DayNumber - from.DayNumber > LongestWindowDays)
        {
            return Failure<IReadOnlyList<CalendarItemDto>>(
                MoteeStatusCodes.InvalidRequest,
                "Ask for a shorter stretch — a year at a time at most.");
        }

        return Ok(await calendar.AgendaAsync(
            new CalendarQuery
            {
                From = from,
                To = to,
                EmployeeIds = employeeIds ?? [],
            },
            cancellationToken));
    }

    // The action centre's card. Its own route because it is on the first screen
    // after sign-in and should not need the caller to work out today's date in
    // the company's time zone before it can ask.
    [HttpGet("upcoming")]
    [ProducesResponseType<IReadOnlyList<CalendarItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Upcoming(
        [FromQuery] int days,
        CancellationToken cancellationToken) =>
        Ok(await calendar.UpcomingAsync(days is > 0 and <= 90 ? days : 7, cancellationToken));

    [HttpGet("events/{id:guid}")]
    [ProducesResponseType<CalendarEventDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        CalendarEventDto? found = await calendar.GetAsync(id, cancellationToken);

        // Not found rather than forbidden for an event that is somebody else's.
        // Telling a stranger an event exists but is not theirs is the leak the
        // visibility rule was there to prevent.
        return found is null
            ? Failure<CalendarEventDto>(MoteeStatusCodes.NotFound, "Event not found.")
            : Ok(found);
    }

    [HttpPost("events")]
    [ProducesResponseType<CalendarEventDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        CalendarEventRequest request,
        CancellationToken cancellationToken)
    {
        bool wide = await CanActForAnyoneAsync(cancellationToken);

        if (request.Audience == CalendarAudience.Company && !wide)
        {
            return Failure<CalendarEventDto>(MoteeStatusCodes.Forbidden, CompanyDenied);
        }

        CalendarEventResult result = await calendar.CreateAsync(request, wide, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Event!, "Event added.")
            : Failure<CalendarEventDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("events/{id:guid}")]
    [ProducesResponseType<CalendarEventDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        Guid id,
        CalendarEventRequest request,
        CancellationToken cancellationToken)
    {
        bool wide = await CanActForAnyoneAsync(cancellationToken);

        if (request.Audience == CalendarAudience.Company && !wide)
        {
            return Failure<CalendarEventDto>(MoteeStatusCodes.Forbidden, CompanyDenied);
        }

        CalendarEventResult result = await calendar.UpdateAsync(
            id, request, wide, cancellationToken);

        return result.Succeeded
            ? Ok(result.Event!, "Event updated.")
            : Failure<CalendarEventDto>(StatusFor(result), MessageFor(result));
    }

    [HttpDelete("events/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        CalendarEventOutcome outcome = await calendar.DeleteAsync(
            id, await CanActForAnyoneAsync(cancellationToken), cancellationToken);

        return outcome switch
        {
            CalendarEventOutcome.Succeeded => Ok<object?>(null, "Event removed."),
            CalendarEventOutcome.NotFound =>
                Failure<object?>(MoteeStatusCodes.NotFound, "Event not found."),
            _ => Failure<object?>(
                MoteeStatusCodes.Forbidden, "Only whoever added this event can remove it."),
        };
    }

    private const string CompanyDenied =
        "Putting something on the company calendar needs the calendar permission. "
        + "You can still add it to your own calendar, or your team's.";

    // Resolved here rather than as an attribute because one endpoint serves
    // both cases: an employee adding a one-to-one and HR announcing a shutdown
    // arrive at the same route, and only the body says which.
    private async Task<bool> CanActForAnyoneAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            return false;
        }

        ResolvedPermissions held = await userPermissions.ForAsync(userId, cancellationToken);

        return held.IsOwner
            || AccessDecision.Resolve(held.Permissions, Module, PermissionAction.Create)
                .Kind != DataScopeKind.None;
    }

    private static string StatusFor(CalendarEventResult result) => result.Outcome switch
    {
        CalendarEventOutcome.NotFound => MoteeStatusCodes.NotFound,
        CalendarEventOutcome.EmployeeNotFound => MoteeStatusCodes.NotFound,
        CalendarEventOutcome.NotYoursToAddTo or CalendarEventOutcome.NotYoursToChange =>
            MoteeStatusCodes.Forbidden,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(CalendarEventResult result) => result.Outcome switch
    {
        CalendarEventOutcome.NotFound => "Event not found.",
        CalendarEventOutcome.EmployeeNotFound => "One of those people does not work here.",
        CalendarEventOutcome.NotYoursToAddTo =>
            "You can add events to your own calendar and to your team's.",
        CalendarEventOutcome.NotYoursToChange =>
            "Only whoever added this event can change it.",
        _ => ReasonFor(result.Refusal),
    };

    // Sentences, because they are shown as written: somebody filling in a
    // dialog needs to know what to change, not which rule stopped them.
    private static string ReasonFor(CalendarRefusal refusal) => refusal switch
    {
        CalendarRefusal.TitleRequired => "Give the event a title.",
        CalendarRefusal.EndsBeforeItStarts => "The event ends before it starts.",
        CalendarRefusal.StartTimeRequired =>
            "Give it a start time, or mark it as all day.",
        CalendarRefusal.EndTimeNotAfterStart => "The end time has to be after the start.",
        CalendarRefusal.NobodyToAddItTo => "Choose whose calendar this goes on.",
        CalendarRefusal.CompanyEventTakesNoAttendees =>
            "A company event already goes to everybody, so it takes no guest list.",
        CalendarRefusal.CategoryNotChoosable =>
            "That kind of entry comes from elsewhere — leave from a leave request, "
            + "a holiday from the holiday calendar — so it cannot be typed in here.",
        CalendarRefusal.LongerThanAYear =>
            "That runs for more than a year. Check the end date.",
        _ => "Could not save the event.",
    };
}
