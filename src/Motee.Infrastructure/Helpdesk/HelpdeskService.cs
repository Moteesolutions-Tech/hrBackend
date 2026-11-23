using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Helpdesk;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Helpdesk;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Helpdesk;

// The HR help desk.
//
// Internal notes are filtered out of the payload rather than flagged in it:
// a submitter reading their own ticket gets the conversation without them. The
// SLA deadline, whether the ticket is overdue and when it was first answered
// are all derived, so reprioritising a ticket moves its deadline instead of
// leaving the one it was given on the day it was raised.
internal sealed class HelpdeskService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : IHelpdeskService
{
    private const int MineTake = 100;

    public async Task<PagedResult<TicketDto>> ListAsync(
        TicketQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<HelpdeskTicket> tickets = dbContext.HelpdeskTickets.AsNoTracking();

        if (query.Status is TicketStatus status)
        {
            tickets = tickets.Where(ticket => ticket.Status == status);
        }
        else if (query.OpenOnly == true)
        {
            tickets = tickets.Where(ticket =>
                ticket.Status != TicketStatus.Resolved && ticket.Status != TicketStatus.Closed);
        }

        if (query.Category is TicketCategory category)
        {
            tickets = tickets.Where(ticket => ticket.Category == category);
        }

        if (query.Priority is TicketPriority priority)
        {
            tickets = tickets.Where(ticket => ticket.Priority == priority);
        }

        if (query.AssigneeEmployeeId is Guid assigneeId)
        {
            tickets = tickets.Where(ticket => ticket.AssigneeEmployeeId == assigneeId);
        }
        else if (query.UnassignedOnly == true)
        {
            tickets = tickets.Where(ticket => ticket.AssigneeEmployeeId == null);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            tickets = tickets.Where(ticket =>
                EF.Functions.ILike(ticket.Reference, pattern, "\\")
                || EF.Functions.ILike(ticket.Subject, pattern, "\\")
                || EF.Functions.ILike(ticket.Description, pattern, "\\"));
        }

        // Overdue is derived from the priority and the clock, so it cannot be
        // filtered in SQL. Everything above narrows first.
        List<HelpdeskTicket> rows = await tickets
            .OrderByDescending(ticket => ticket.Priority)
            .ThenBy(ticket => ticket.CreatedAt)
            .ToListAsync(cancellationToken);

        List<TicketDto> described = await DescribeAsync(rows, asAgent: true, cancellationToken);

        if (query.OverdueOnly == true)
        {
            described = [.. described.Where(ticket => ticket.IsOverdue)];
        }

        return new PagedResult<TicketDto>
        {
            Items = [.. described.Skip(query.Skip).Take(query.PageSize)],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = described.Count,
        };
    }

    public async Task<IReadOnlyList<TicketDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return [];
        }

        List<HelpdeskTicket> rows = await dbContext.HelpdeskTickets
            .AsNoTracking()
            .Where(ticket => ticket.SubmitterEmployeeId == me)
            .OrderByDescending(ticket => ticket.CreatedAt)
            .Take(MineTake)
            .ToListAsync(cancellationToken);

        // Their own ticket, without the agents' notes.
        return await DescribeAsync(rows, asAgent: false, cancellationToken);
    }

    public async Task<TicketDto?> GetAsync(
        Guid id,
        bool asAgent,
        CancellationToken cancellationToken = default)
    {
        HelpdeskTicket? ticket = await dbContext.HelpdeskTickets
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (ticket is null)
        {
            return null;
        }

        Guid me = await currentEmployee.IdAsync(cancellationToken) ?? Guid.Empty;

        // Somebody who is neither an agent nor the person who raised it has no
        // business reading a help desk conversation.
        if (!asAgent && ticket.SubmitterEmployeeId != me)
        {
            return null;
        }

        return (await DescribeAsync([ticket], asAgent, cancellationToken))[0];
    }

    public async Task<TicketTally> SummaryAsync(CancellationToken cancellationToken = default)
    {
        List<HelpdeskTicket> tickets = await dbContext.HelpdeskTickets
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (tickets.Count == 0)
        {
            return TicketQueue.Of([]);
        }

        List<Guid> ids = [.. tickets.Select(ticket => ticket.Id)];

        Dictionary<Guid, DateTimeOffset> firstReplies =
            await FirstRepliesAsync(ids, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        return TicketQueue.Of(
            [
                .. tickets.Select(ticket => new TicketFacts
                {
                    Status = ticket.Status,
                    IsOverdue = Tickets.IsOverdue(ticket, now),
                    MetSla = Tickets.MetSla(ticket),
                    HoursToFirstReply = firstReplies.TryGetValue(ticket.Id, out DateTimeOffset at)
                        ? Hours(ticket.CreatedAt, at)
                        : null,
                }),
            ]);
    }

    public async Task<TicketResult> RaiseAsync(
        TicketRequest request,
        bool asAgent,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return TicketResult.Failed(TicketOutcome.NoEmployeeRecord);
        }

        Guid submitterId = request.SubmitterEmployeeId ?? me;

        // Recording a ticket for somebody else is an agent's job.
        if (submitterId != me && !asAgent)
        {
            return TicketResult.Failed(TicketOutcome.Forbidden);
        }

        if (submitterId != me)
        {
            bool employed = await dbContext.Employees.AnyAsync(
                employee => employee.Id == submitterId
                    && employee.Status != EmployeeStatus.Deleted,
                cancellationToken);

            if (!employed)
            {
                return TicketResult.Failed(TicketOutcome.EmployeeNotFound);
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        HelpdeskTicket ticket = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Reference = await NextReferenceAsync(now.Year, cancellationToken),
            Subject = request.Subject?.Trim() ?? string.Empty,
            Description = request.Description?.Trim() ?? string.Empty,
            Category = request.Category,
            Priority = request.Priority,
            Status = TicketStatus.Open,
            SubmitterEmployeeId = submitterId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        TicketRefusal refusal = Tickets.Refusal(ticket);

        if (refusal != TicketRefusal.None)
        {
            return TicketResult.Refused(refusal);
        }

        dbContext.HelpdeskTickets.Add(ticket);

        // The description is the first message, so the conversation reads in
        // one place rather than the opening line living on the ticket and
        // every reply living somewhere else.
        dbContext.TicketMessages.Add(new TicketMessage
        {
            Id = Guid.NewGuid(),
            TenantId = ticket.TenantId,
            TicketId = ticket.Id,
            AuthorEmployeeId = submitterId,
            Body = ticket.Description,
            IsFromAgent = false,
            IsInternalNote = false,
            CreatedAt = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return TicketResult.Ok((await DescribeAsync([ticket], asAgent, cancellationToken))[0]);
    }

    public async Task<TicketResult> ReplyAsync(
        Guid id,
        TicketReplyRequest request,
        bool asAgent,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return TicketResult.Failed(TicketOutcome.NoEmployeeRecord);
        }

        HelpdeskTicket? ticket = await FindAsync(id, cancellationToken);

        if (ticket is null)
        {
            return TicketResult.Failed(TicketOutcome.NotFound);
        }

        if (!asAgent && ticket.SubmitterEmployeeId != me)
        {
            return TicketResult.Refused(TicketRefusal.NotYours);
        }

        if (ticket.Status == TicketStatus.Closed)
        {
            return TicketResult.Refused(TicketRefusal.AlreadyClosed);
        }

        // An internal note is a note between agents. Without the grant it
        // would be a message the author believes is private and the submitter
        // can read.
        if (request.IsInternalNote && !asAgent)
        {
            return TicketResult.Failed(TicketOutcome.Forbidden);
        }

        TicketRefusal refusal = Tickets.MessageRefusal(request.Body);

        if (refusal != TicketRefusal.None)
        {
            return TicketResult.Refused(refusal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        dbContext.TicketMessages.Add(new TicketMessage
        {
            Id = Guid.NewGuid(),
            TenantId = ticket.TenantId,
            TicketId = id,
            AuthorEmployeeId = me,

            // Frozen from the grant the author held when they wrote it, rather
            // than guessed later from their department's name.
            IsFromAgent = asAgent,
            IsInternalNote = request.IsInternalNote,
            Body = request.Body.Trim(),
            CreatedAt = now,
        });

        if (request.Status is TicketStatus status)
        {
            ticket.Status = status;
            ticket.ResolvedAt = Tickets.ResolvedAt(status, ticket.ResolvedAt, now);
        }
        else if (!asAgent && ticket.Status == TicketStatus.PendingResponse)
        {
            // The employee has answered, so it is our turn again. Leaving it
            // waiting on them is how a ticket sits in nobody's queue.
            ticket.Status = TicketStatus.InProgress;
        }

        ticket.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return TicketResult.Ok((await DescribeAsync([ticket], asAgent, cancellationToken))[0]);
    }

    public async Task<TicketResult> SetStatusAsync(
        Guid id,
        TicketStatus status,
        CancellationToken cancellationToken = default)
    {
        HelpdeskTicket? ticket = await FindAsync(id, cancellationToken);

        if (ticket is null)
        {
            return TicketResult.Failed(TicketOutcome.NotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ticket.Status = status;

        // Stamped on the way in and cleared on the way out, so the date and
        // the status cannot disagree — and the SLA verdict is judged against
        // the date it was settled rather than against now.
        ticket.ResolvedAt = Tickets.ResolvedAt(status, ticket.ResolvedAt, now);
        ticket.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return TicketResult.Ok((await DescribeAsync([ticket], asAgent: true, cancellationToken))[0]);
    }

    public async Task<TicketResult> SetPriorityAsync(
        Guid id,
        TicketPriority priority,
        CancellationToken cancellationToken = default)
    {
        HelpdeskTicket? ticket = await FindAsync(id, cancellationToken);

        if (ticket is null)
        {
            return TicketResult.Failed(TicketOutcome.NotFound);
        }

        // Nothing else to write: the deadline follows the priority, so this
        // one field moves it.
        ticket.Priority = priority;
        ticket.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return TicketResult.Ok((await DescribeAsync([ticket], asAgent: true, cancellationToken))[0]);
    }

    public async Task<TicketResult> AssignAsync(
        Guid id,
        Guid? assigneeEmployeeId,
        CancellationToken cancellationToken = default)
    {
        HelpdeskTicket? ticket = await FindAsync(id, cancellationToken);

        if (ticket is null)
        {
            return TicketResult.Failed(TicketOutcome.NotFound);
        }

        if (assigneeEmployeeId is Guid assigneeId)
        {
            bool employed = await dbContext.Employees.AnyAsync(
                employee => employee.Id == assigneeId
                    && employee.Status != EmployeeStatus.Deleted,
                cancellationToken);

            if (!employed)
            {
                return TicketResult.Failed(TicketOutcome.EmployeeNotFound);
            }
        }

        ticket.AssigneeEmployeeId = assigneeEmployeeId;

        // Picking a ticket up moves it out of the open queue, which is what
        // assigning one means.
        if (assigneeEmployeeId is not null && ticket.Status == TicketStatus.Open)
        {
            ticket.Status = TicketStatus.InProgress;
        }

        ticket.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return TicketResult.Ok((await DescribeAsync([ticket], asAgent: true, cancellationToken))[0]);
    }

    public async Task<TicketResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        HelpdeskTicket? ticket = await FindAsync(id, cancellationToken);

        if (ticket is null)
        {
            return TicketResult.Failed(TicketOutcome.NotFound);
        }

        dbContext.HelpdeskTickets.Remove(ticket);

        await dbContext.SaveChangesAsync(cancellationToken);

        return TicketResult.Done();
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<HelpdeskTicket?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.HelpdeskTickets.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    // When each ticket was first answered by the help desk, which is what the
    // first-response figure measures. An internal note is not an answer to
    // the person waiting.
    private async Task<Dictionary<Guid, DateTimeOffset>> FirstRepliesAsync(
        IReadOnlyList<Guid> ticketIds,
        CancellationToken cancellationToken) =>
        await dbContext.TicketMessages
            .AsNoTracking()
            .Where(message => ticketIds.Contains(message.TicketId)
                && message.IsFromAgent
                && !message.IsInternalNote)
            .GroupBy(message => message.TicketId)
            .Select(group => new
            {
                TicketId = group.Key,
                At = group.Min(message => message.CreatedAt),
            })
            .ToDictionaryAsync(row => row.TicketId, row => row.At, cancellationToken);

    private static decimal Hours(DateTimeOffset from, DateTimeOffset to) =>
        Math.Round((decimal)(to - from).TotalHours, 1, MidpointRounding.AwayFromZero);

    private async Task<List<TicketDto>> DescribeAsync(
        IReadOnlyList<HelpdeskTicket> tickets,
        bool asAgent,
        CancellationToken cancellationToken)
    {
        if (tickets.Count == 0)
        {
            return [];
        }

        Guid me = await currentEmployee.IdAsync(cancellationToken) ?? Guid.Empty;

        List<Guid> ids = [.. tickets.Select(ticket => ticket.Id)];

        // Only what this reader may see. An internal note is filtered out of
        // the query rather than flagged in the payload.
        List<TicketMessage> messages = await dbContext.TicketMessages
            .AsNoTracking()
            .Where(message => ids.Contains(message.TicketId)
                && (asAgent || !message.IsInternalNote))
            .OrderBy(message => message.CreatedAt)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, DateTimeOffset> firstReplies =
            await FirstRepliesAsync(ids, cancellationToken);

        List<Guid> employeeIds =
        [
            .. tickets.Select(ticket => ticket.SubmitterEmployeeId)
                .Concat(tickets.Where(ticket => ticket.AssigneeEmployeeId is not null)
                    .Select(ticket => ticket.AssigneeEmployeeId!.Value))
                .Concat(messages.Select(message => message.AuthorEmployeeId))
                .Distinct(),
        ];

        Dictionary<Guid, Person> people = await PeopleAsync(employeeIds, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        return
        [
            .. tickets.Select(ticket =>
            {
                DateTimeOffset? firstReply =
                    firstReplies.TryGetValue(ticket.Id, out DateTimeOffset at) ? at : null;

                return new TicketDto
                {
                    Id = ticket.Id,
                    Reference = ticket.Reference,
                    Subject = ticket.Subject,
                    Description = ticket.Description,
                    Category = ticket.Category,
                    Priority = ticket.Priority,
                    Status = ticket.Status,
                    SubmitterEmployeeId = ticket.SubmitterEmployeeId,
                    SubmitterName = people.GetValueOrDefault(ticket.SubmitterEmployeeId)?.Name,
                    SubmitterDepartmentName =
                        people.GetValueOrDefault(ticket.SubmitterEmployeeId)?.DepartmentName,
                    AssigneeEmployeeId = ticket.AssigneeEmployeeId,
                    AssigneeName = ticket.AssigneeEmployeeId is Guid assigneeId
                        ? people.GetValueOrDefault(assigneeId)?.Name
                        : null,
                    SlaDueAt = Tickets.SlaDueAt(ticket),
                    IsOverdue = Tickets.IsOverdue(ticket, now),
                    MetSla = Tickets.MetSla(ticket),
                    FirstReplyAt = firstReply,
                    HoursToFirstReply = firstReply is DateTimeOffset replied
                        ? Hours(ticket.CreatedAt, replied)
                        : null,

                    // Whose turn it is, from the caller's side.
                    IsWaitingOnMe = asAgent
                        ? !Tickets.IsWaitingOnSubmitter(ticket.Status)
                            && !Tickets.IsFinished(ticket.Status)
                        : Tickets.IsWaitingOnSubmitter(ticket.Status)
                            && ticket.SubmitterEmployeeId == me,
                    ResolvedAt = ticket.ResolvedAt,
                    Messages =
                    [
                        .. messages
                            .Where(message => message.TicketId == ticket.Id)
                            .Select(message => new TicketMessageDto
                            {
                                Id = message.Id,
                                AuthorEmployeeId = message.AuthorEmployeeId,
                                AuthorName =
                                    people.GetValueOrDefault(message.AuthorEmployeeId)?.Name,
                                AuthorDepartmentName = people
                                    .GetValueOrDefault(message.AuthorEmployeeId)?.DepartmentName,
                                Body = message.Body,
                                IsFromAgent = message.IsFromAgent,
                                IsInternalNote = message.IsInternalNote,
                                CreatedAt = message.CreatedAt,
                            }),
                    ],
                    CreatedAt = ticket.CreatedAt,
                    UpdatedAt = ticket.UpdatedAt,
                };
            }),
        ];
    }

    private async Task<Dictionary<Guid, Person>> PeopleAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id,
                department => department.Name,
                cancellationToken);

        return await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                employee.DepartmentId,
            })
            .ToDictionaryAsync(
                row => row.Id,
                row => new Person(
                    row.Name,
                    row.DepartmentId is Guid id ? departments.GetValueOrDefault(id) : null),
                cancellationToken);
    }

    // TKT-2026-00042. Counted off what this tenant has issued this year, read
    // inside the same save as the write that claims it — the unique index is
    // what makes two tabs safe.
    private async Task<string> NextReferenceAsync(int year, CancellationToken cancellationToken)
    {
        string prefix = $"TKT-{year}-";

        List<string> issued = await dbContext.HelpdeskTickets
            .AsNoTracking()
            .Where(ticket => ticket.Reference.StartsWith(prefix))
            .Select(ticket => ticket.Reference)
            .ToListAsync(cancellationToken);

        int highest = issued
            .Select(reference =>
                int.TryParse(reference[prefix.Length..], out int number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();

        return Tickets.Reference(year, highest + 1);
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed record Person(string Name, string? DepartmentName);
}
