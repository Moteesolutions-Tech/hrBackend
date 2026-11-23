using Motee.Domain.Common;

namespace Motee.Domain.Helpdesk;

public enum TicketCategory
{
    Payroll,
    Leave,
    ItSupport,
    HrPolicy,
    Benefits,
    Onboarding,
    Offboarding,
    Other,
}

public enum TicketPriority
{
    Low,
    Medium,
    High,
    Urgent,
}

public enum TicketStatus
{
    Open,
    InProgress,

    // Waiting on us — a third party, a payroll run, a decision elsewhere.
    Pending,

    // Waiting on the person who raised it. The client carries both of these
    // and nothing says which way round they are; this is the distinction, and
    // it is the one that decides whose turn it is.
    PendingResponse,

    Resolved,
    Closed,
}

// One help desk ticket.
//
// The SLA deadline, whether it is overdue, and when it was first answered are
// all derived. The client stores the first two, so reprioritising a ticket
// leaves yesterday's deadline in place and an overdue flag that nothing
// recalculates.
public class HelpdeskTicket : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // TKT-2026-00042. Quotable in an email, which an opaque id is not.
    public required string Reference { get; set; }

    public required string Subject { get; set; }

    public required string Description { get; set; }

    public TicketCategory Category { get; set; }

    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    public TicketStatus Status { get; set; } = TicketStatus.Open;

    public Guid SubmitterEmployeeId { get; set; }

    // Whoever picked it up. Null while it is in the open queue.
    public Guid? AssigneeEmployeeId { get; set; }

    // Stamped by the move into resolved or closed and cleared by the move out,
    // so the date and the status cannot disagree.
    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One message on a ticket.
public class TicketMessage : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TicketId { get; set; }

    public Guid AuthorEmployeeId { get; set; }

    public required string Body { get; set; }

    // Whether this came from the help desk rather than the person who raised
    // the ticket. Decided by the author's permission when they posted and then
    // frozen — the client works it out by checking whether the author's
    // department name contains the word "people", which is a string match on a
    // label anybody can rename, and which makes everybody in the People team
    // an agent whether or not they hold the module.
    public bool IsFromAgent { get; set; }

    // An agent's note to other agents. Never leaves the service for the person
    // who raised the ticket.
    public bool IsInternalNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum TicketRefusal
{
    None,
    SubjectRequired,
    SubjectTooLong,
    DescriptionRequired,
    DescriptionTooLong,
    MessageRequired,
    MessageTooLong,

    // Replying to a ticket that is closed.
    AlreadyClosed,

    // Someone who is neither the submitter nor an agent.
    NotYours,
}

public static class TicketCategories
{
    // The client's mapper folds these older spellings in, so a row written by
    // an earlier version keeps reading.
    public static bool TryParse(string? value, out TicketCategory category)
    {
        category = TicketCategory.Other;

        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "it":
            case "tech":
            case "it_support":
            case "itsupport":
                category = TicketCategory.ItSupport;
                return true;
            case "hr":
            case "hr_policy":
            case "hrpolicy":
                category = TicketCategory.HrPolicy;
                return true;
            case "finance":
            case "payroll":
                category = TicketCategory.Payroll;
                return true;
            case "facilities":
            case "other":
                return true;
            case "leave":
                category = TicketCategory.Leave;
                return true;
            case "benefits":
                category = TicketCategory.Benefits;
                return true;
            case "onboarding":
                category = TicketCategory.Onboarding;
                return true;
            case "offboarding":
                category = TicketCategory.Offboarding;
                return true;
            default:
                return false;
        }
    }
}

public static class TicketPriorities
{
    public static bool TryParse(string? value, out TicketPriority priority)
    {
        priority = TicketPriority.Medium;

        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "critical":
            case "urgent":
                priority = TicketPriority.Urgent;
                return true;
            case "high":
                priority = TicketPriority.High;
                return true;
            case "normal":
            case "medium":
                return true;
            case "low":
                priority = TicketPriority.Low;
                return true;
            default:
                return false;
        }
    }
}

public static class Tickets
{
    public const int ShortestSubject = 5;

    public const int LongestSubject = 200;

    public const int LongestDescription = 4000;

    public const int LongestMessage = 4000;

    // The intervals the client's own hardcoded dates imply, measured from the
    // day a ticket was raised. Its map is keyed on critical/high/normal/low
    // while the priority enum is low/medium/high/urgent — so today an urgent
    // ticket and a medium one both fall through to the same default.
    public static TimeSpan SlaTargetFor(TicketPriority priority) => priority switch
    {
        TicketPriority.Urgent => TimeSpan.FromDays(1),
        TicketPriority.High => TimeSpan.FromDays(2),
        TicketPriority.Low => TimeSpan.FromDays(14),
        _ => TimeSpan.FromDays(7),
    };

    // Derived, so raising the priority pulls the deadline in rather than
    // leaving yesterday's.
    public static DateTimeOffset SlaDueAt(HelpdeskTicket ticket) =>
        ticket.CreatedAt + SlaTargetFor(ticket.Priority);

    public static bool IsFinished(TicketStatus status) =>
        status is TicketStatus.Resolved or TicketStatus.Closed;

    // Waiting on the person who raised it, rather than on us.
    public static bool IsWaitingOnSubmitter(TicketStatus status) =>
        status == TicketStatus.PendingResponse;

    // Lateness is about whether it is still open. A ticket answered three
    // weeks late is closed, not perpetually breaching.
    public static bool IsOverdue(HelpdeskTicket ticket, DateTimeOffset asAt) =>
        !IsFinished(ticket.Status) && asAt > SlaDueAt(ticket);

    // Whether the SLA was met, judged on when it was settled rather than on
    // now. Null while it is still open — there is nothing to judge yet, and
    // counting it either way would move the compliance figure for no reason.
    public static bool? MetSla(HelpdeskTicket ticket)
    {
        if (!IsFinished(ticket.Status) || ticket.ResolvedAt is not DateTimeOffset settled)
        {
            return null;
        }

        return settled <= SlaDueAt(ticket);
    }

    public static DateTimeOffset? ResolvedAt(
        TicketStatus status,
        DateTimeOffset? was,
        DateTimeOffset asAt)
    {
        if (!IsFinished(status))
        {
            return null;
        }

        // Closing a resolved ticket keeps the moment it was resolved, which is
        // the date the SLA is judged against.
        return was ?? asAt;
    }

    public static TicketRefusal Refusal(HelpdeskTicket ticket)
    {
        string subject = ticket.Subject?.Trim() ?? string.Empty;
        string description = ticket.Description?.Trim() ?? string.Empty;

        if (subject.Length < ShortestSubject)
        {
            return TicketRefusal.SubjectRequired;
        }

        if (subject.Length > LongestSubject)
        {
            return TicketRefusal.SubjectTooLong;
        }

        if (description.Length == 0)
        {
            return TicketRefusal.DescriptionRequired;
        }

        return description.Length > LongestDescription
            ? TicketRefusal.DescriptionTooLong
            : TicketRefusal.None;
    }

    public static TicketRefusal MessageRefusal(string? body)
    {
        string trimmed = body?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return TicketRefusal.MessageRequired;
        }

        return trimmed.Length > LongestMessage
            ? TicketRefusal.MessageTooLong
            : TicketRefusal.None;
    }

    public static string Reference(int year, int sequence) => $"TKT-{year}-{sequence:D5}";
}

// One ticket, reduced to what the queue's figures need.
public sealed record TicketFacts
{
    public required TicketStatus Status { get; init; }

    public required bool IsOverdue { get; init; }

    // Null while the ticket is still open.
    public bool? MetSla { get; init; }

    public decimal? HoursToFirstReply { get; init; }
}

public sealed record TicketTally
{
    public required int Total { get; init; }

    public required int Open { get; init; }

    public required int InProgress { get; init; }

    // Waiting on us.
    public required int Pending { get; init; }

    // Waiting on the person who raised it, which is not the same queue.
    public required int AwaitingEmployee { get; init; }

    public required int Resolved { get; init; }

    public required int Closed { get; init; }

    public required int Overdue { get; init; }

    // Over the tickets that have actually been settled. Null before any have
    // been — not a hundred per cent, which would read as a perfect record on
    // the day a company starts using the help desk.
    public decimal? SlaCompliance { get; init; }

    public decimal? AverageHoursToFirstReply { get; init; }
}

public static class TicketQueue
{
    public static TicketTally Of(IReadOnlyList<TicketFacts> tickets)
    {
        List<bool> settled = [.. tickets.Select(row => row.MetSla).OfType<bool>()];

        List<decimal> replies =
            [.. tickets.Select(row => row.HoursToFirstReply).OfType<decimal>()];

        return new TicketTally
        {
            Total = tickets.Count,
            Open = tickets.Count(row => row.Status == TicketStatus.Open),
            InProgress = tickets.Count(row => row.Status == TicketStatus.InProgress),
            Pending = tickets.Count(row => row.Status == TicketStatus.Pending),
            AwaitingEmployee = tickets.Count(row =>
                row.Status == TicketStatus.PendingResponse),
            Resolved = tickets.Count(row => row.Status == TicketStatus.Resolved),
            Closed = tickets.Count(row => row.Status == TicketStatus.Closed),
            Overdue = tickets.Count(row => row.IsOverdue),
            SlaCompliance = settled.Count == 0
                ? null
                : Math.Round(
                    settled.Count(met => met) / (decimal)settled.Count * 100m,
                    1,
                    MidpointRounding.AwayFromZero),
            AverageHoursToFirstReply = replies.Count == 0
                ? null
                : Math.Round(replies.Average(), 1, MidpointRounding.AwayFromZero),
        };
    }
}
