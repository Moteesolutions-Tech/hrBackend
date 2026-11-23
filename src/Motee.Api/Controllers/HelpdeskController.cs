using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Helpdesk;
using Motee.Domain.Authorization;
using Motee.Domain.Helpdesk;
using Motee.Domain.Identity;

namespace Motee.Api.Controllers;

// The HR help desk.
//
// Whether the caller is an agent is resolved here and passed into the service,
// because it changes what comes back rather than whether the call is allowed:
// an employee reading their own ticket gets the conversation without the
// agents' internal notes. Raising and replying need no module grant — a help
// desk nobody can write to is not a help desk.
//
// There is no FAQ route. The FAQ panel is a read-only list of categorised
// answers with helpful votes, which is the knowledge base: it should read
// GET /knowledge/published rather than a second store of the same articles.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/helpdesk")]
public class HelpdeskController(
    IHelpdeskService helpdesk,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "workspace.helpdesk";

    [HttpGet]
    [ProducesResponseType<PagedResult<TicketDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] TicketQuery query,
        CancellationToken cancellationToken) =>
        Ok(await helpdesk.ListAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<TicketTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await helpdesk.SummaryAsync(cancellationToken));

    // The caller's own tickets, without the agents' notes.
    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<TicketDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await helpdesk.MineAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType<TicketDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        TicketDto? found = await helpdesk.GetAsync(
            id, await IsAgentAsync(cancellationToken), cancellationToken);

        return found is null
            ? Failure<TicketDto>(MoteeStatusCodes.NotFound, "Ticket not found.")
            : Ok(found);
    }

    // Anybody may raise one for themselves. Recording one for somebody else
    // is an agent's job.
    [HttpPost]
    [Authorize]
    [ProducesResponseType<TicketDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Raise(
        TicketRequest request,
        CancellationToken cancellationToken)
    {
        TicketResult result = await helpdesk.RaiseAsync(
            request, await IsAgentAsync(cancellationToken), cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(
                result.Ticket!,
                $"Ticket {result.Ticket!.Reference} raised. Quote that reference if you "
                + "need to chase it.")
            : Failure<TicketDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPost("{id:guid}/messages")]
    [Authorize]
    [ProducesResponseType<TicketDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Reply(
        Guid id,
        TicketReplyRequest request,
        CancellationToken cancellationToken)
    {
        TicketResult result = await helpdesk.ReplyAsync(
            id, request, await IsAgentAsync(cancellationToken), cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(
                result.Ticket!,
                request.IsInternalNote ? "Note added — only the team can see it." : "Reply sent.")
            : Failure<TicketDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPost("{id:guid}/status")]
    [ProducesResponseType<TicketDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetStatus(
        Guid id,
        TicketStatusRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await helpdesk.SetStatusAsync(id, request.Status, cancellationToken),
            request.Status switch
            {
                TicketStatus.Resolved => "Ticket resolved.",
                TicketStatus.Closed => "Ticket closed.",
                TicketStatus.PendingResponse => "Waiting on the employee.",
                TicketStatus.Pending => "Ticket on hold.",
                _ => "Ticket updated.",
            });

    // One field, because the deadline follows the priority rather than being
    // written beside it.
    [HttpPost("{id:guid}/priority")]
    [ProducesResponseType<TicketDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetPriority(
        Guid id,
        TicketPriorityRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await helpdesk.SetPriorityAsync(id, request.Priority, cancellationToken),
            "Priority updated, and the SLA deadline with it.");

    // An omitted assignee unassigns, which is how a ticket goes back to the
    // open queue.
    [HttpPost("{id:guid}/assign")]
    [ProducesResponseType<TicketDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Assign(
        Guid id,
        AssignTicketRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await helpdesk.AssignAsync(id, request?.AssigneeEmployeeId, cancellationToken),
            request?.AssigneeEmployeeId is null
                ? "Ticket returned to the queue."
                : "Ticket assigned.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        TicketResult result = await helpdesk.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Ticket deleted.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    private IActionResult Respond(TicketResult result, string message) =>
        result.Succeeded
            ? Ok(result.Ticket!, message)
            : Failure<TicketDto>(StatusFor(result), MessageFor(result));

    // Resolved here rather than as an attribute because the same routes serve
    // the person who raised a ticket and the team answering it, and the
    // difference is what the payload contains.
    private async Task<bool> IsAgentAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            return false;
        }

        ResolvedPermissions held = await userPermissions.ForAsync(userId, cancellationToken);

        return held.IsOwner
            || AccessDecision.Resolve(held.Permissions, Module, PermissionAction.Edit)
                .Kind != DataScopeKind.None;
    }

    private static string StatusFor(TicketResult result) => result.Outcome switch
    {
        TicketOutcome.NotFound or TicketOutcome.EmployeeNotFound => MoteeStatusCodes.NotFound,
        TicketOutcome.Forbidden or TicketOutcome.NoEmployeeRecord => MoteeStatusCodes.Forbidden,

        TicketOutcome.Refused when result.Refusal
            is TicketRefusal.AlreadyClosed => MoteeStatusCodes.Conflict,

        TicketOutcome.Refused when result.Refusal
            is TicketRefusal.NotYours => MoteeStatusCodes.Forbidden,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(TicketResult result) => result.Outcome switch
    {
        TicketOutcome.NotFound => "Ticket not found.",
        TicketOutcome.EmployeeNotFound => "That person does not work here.",
        TicketOutcome.Forbidden =>
            "Raising a ticket for somebody else, or leaving an internal note, needs help "
            + "desk access.",
        TicketOutcome.NoEmployeeRecord =>
            "Your account has no employee record, so it cannot raise a ticket.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(TicketRefusal refusal) => refusal switch
    {
        TicketRefusal.SubjectRequired =>
            $"Say what this is about in at least {Tickets.ShortestSubject} characters.",
        TicketRefusal.SubjectTooLong =>
            $"Keep the subject to {Tickets.LongestSubject} characters or fewer.",
        TicketRefusal.DescriptionRequired => "Describe what you need help with.",
        TicketRefusal.DescriptionTooLong =>
            $"Keep the description to {Tickets.LongestDescription:N0} characters or fewer.",
        TicketRefusal.MessageRequired => "Write something before sending.",
        TicketRefusal.MessageTooLong =>
            $"Keep the message to {Tickets.LongestMessage:N0} characters or fewer.",
        TicketRefusal.AlreadyClosed =>
            "This ticket is closed. Raise a new one and quote the old reference.",
        TicketRefusal.NotYours => "This ticket is not yours.",
        _ => "Could not save the ticket.",
    };
}

public sealed record TicketStatusRequest
{
    public required TicketStatus Status { get; init; }
}

public sealed record TicketPriorityRequest
{
    public required TicketPriority Priority { get; init; }
}

public sealed record AssignTicketRequest
{
    // Omitted unassigns.
    public Guid? AssigneeEmployeeId { get; init; }
}
