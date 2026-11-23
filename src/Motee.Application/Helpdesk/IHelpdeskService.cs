using Motee.Application.Common;
using Motee.Domain.Helpdesk;

namespace Motee.Application.Helpdesk;

// The HR help desk.
//
// Internal notes never leave the service for the person who raised the ticket.
// `asAgent` is the caller's module grant, resolved in the controller and
// passed down — so a submitter reading their own ticket gets the conversation
// without the notes, rather than the client being trusted to hide them.
public interface IHelpdeskService
{
    // The agents' queue.
    Task<PagedResult<TicketDto>> ListAsync(
        TicketQuery query,
        CancellationToken cancellationToken = default);

    // The caller's own tickets, whatever their permissions.
    Task<IReadOnlyList<TicketDto>> MineAsync(CancellationToken cancellationToken = default);

    Task<TicketDto?> GetAsync(
        Guid id,
        bool asAgent,
        CancellationToken cancellationToken = default);

    // The queue's figures, including SLA compliance over settled tickets.
    Task<TicketTally> SummaryAsync(CancellationToken cancellationToken = default);

    // Raised by the employee, or by an agent on their behalf.
    Task<TicketResult> RaiseAsync(
        TicketRequest request,
        bool asAgent,
        CancellationToken cancellationToken = default);

    // A reply, or an agent's internal note. The caller must be the submitter
    // or an agent; an internal note needs the grant.
    Task<TicketResult> ReplyAsync(
        Guid id,
        TicketReplyRequest request,
        bool asAgent,
        CancellationToken cancellationToken = default);

    Task<TicketResult> SetStatusAsync(
        Guid id,
        TicketStatus status,
        CancellationToken cancellationToken = default);

    Task<TicketResult> SetPriorityAsync(
        Guid id,
        TicketPriority priority,
        CancellationToken cancellationToken = default);

    // Null unassigns, which is how a ticket goes back to the open queue.
    Task<TicketResult> AssignAsync(
        Guid id,
        Guid? assigneeEmployeeId,
        CancellationToken cancellationToken = default);

    Task<TicketResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record TicketDto
{
    public required Guid Id { get; init; }

    public required string Reference { get; init; }

    public required string Subject { get; init; }

    public required string Description { get; init; }

    public required TicketCategory Category { get; init; }

    public required TicketPriority Priority { get; init; }

    public required TicketStatus Status { get; init; }

    public required Guid SubmitterEmployeeId { get; init; }

    public string? SubmitterName { get; init; }

    public string? SubmitterDepartmentName { get; init; }

    public Guid? AssigneeEmployeeId { get; init; }

    public string? AssigneeName { get; init; }

    // Derived from the priority and the day it was raised, so reprioritising
    // moves it. The client stores this and leaves it behind.
    public required DateTimeOffset SlaDueAt { get; init; }

    public required bool IsOverdue { get; init; }

    // Whether the SLA was met, judged on when it was settled. Null while the
    // ticket is still open — there is nothing to judge yet.
    public bool? MetSla { get; init; }

    // Derived from the first agent message.
    public DateTimeOffset? FirstReplyAt { get; init; }

    public decimal? HoursToFirstReply { get; init; }

    public required bool IsWaitingOnMe { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    // Internal notes are present only for an agent.
    public required IReadOnlyList<TicketMessageDto> Messages { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record TicketMessageDto
{
    public required Guid Id { get; init; }

    public required Guid AuthorEmployeeId { get; init; }

    public string? AuthorName { get; init; }

    public string? AuthorDepartmentName { get; init; }

    public required string Body { get; init; }

    // Whether it came from the help desk. Frozen at post time from the
    // author's grant, rather than guessed from their department's name.
    public required bool IsFromAgent { get; init; }

    public required bool IsInternalNote { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record TicketQuery : PagedQuery
{
    public TicketStatus? Status { get; init; }

    public TicketCategory? Category { get; init; }

    public TicketPriority? Priority { get; init; }

    public Guid? AssigneeEmployeeId { get; init; }

    // Nobody has picked it up yet.
    public bool? UnassignedOnly { get; init; }

    public bool? OverdueOnly { get; init; }

    // Everything still running, which is the queue's default view.
    public bool? OpenOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record TicketRequest
{
    public required string Subject { get; init; }

    public required string Description { get; init; }

    public TicketCategory Category { get; init; } = TicketCategory.Other;

    public TicketPriority Priority { get; init; } = TicketPriority.Medium;

    // Omitted means the caller is raising it for themselves. Naming somebody
    // else is an agent recording a ticket on their behalf.
    public Guid? SubmitterEmployeeId { get; init; }
}

public sealed record TicketReplyRequest
{
    public required string Body { get; init; }

    // An agent's note to other agents. Refused without the grant.
    public bool IsInternalNote { get; init; }

    // Where a reply also moves the ticket on, which is what an agent
    // answering usually wants.
    public TicketStatus? Status { get; init; }
}

public enum TicketOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,
    Refused,
    Forbidden,
    NoEmployeeRecord,
}

public sealed record TicketResult
{
    public required TicketOutcome Outcome { get; init; }

    public TicketDto? Ticket { get; init; }

    public TicketRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == TicketOutcome.Succeeded;

    public static TicketResult Failed(TicketOutcome outcome) => new() { Outcome = outcome };

    public static TicketResult Refused(TicketRefusal refusal) =>
        new() { Outcome = TicketOutcome.Refused, Refusal = refusal };

    public static TicketResult Ok(TicketDto ticket) =>
        new() { Outcome = TicketOutcome.Succeeded, Ticket = ticket };

    public static TicketResult Done() => new() { Outcome = TicketOutcome.Succeeded };
}
