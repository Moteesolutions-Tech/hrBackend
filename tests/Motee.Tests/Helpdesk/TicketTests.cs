using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Helpdesk;

namespace Motee.Tests.Helpdesk;

// The HR help desk.
//
// Three things the client stores that it should work out, and one it works out
// in a way that will betray it. slaDueAt and isOverdue are columns, so they go
// stale the moment a ticket is reprioritised. firstResponseAt is written by
// hand. And a message is marked as coming from HR by checking whether the
// author's department name contains the word "people" — which is a string
// match on a label anybody can rename.
public class TicketTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly DateTimeOffset Raised =
        new(2026, 4, 4, 9, 0, 0, TimeSpan.Zero);

    private static HelpdeskTicket Ticket(
        TicketPriority priority = TicketPriority.Medium,
        TicketStatus status = TicketStatus.Open,
        TicketCategory category = TicketCategory.Payroll,
        string subject = "March payslip is missing my overtime",
        DateTimeOffset? raisedAt = null,
        DateTimeOffset? resolvedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Reference = "TKT-2026-00001",
            Subject = subject,
            Description = "The overtime I logged on the 14th has not been paid.",
            Category = category,
            Priority = priority,
            Status = status,
            SubmitterEmployeeId = Guid.NewGuid(),
            CreatedAt = raisedAt ?? Raised,
            ResolvedAt = resolvedAt,
        };

    // ── the wire shape ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(TicketCategory.ItSupport, "\"itSupport\"")]
    [InlineData(TicketCategory.HrPolicy, "\"hrPolicy\"")]
    [InlineData(TicketStatus.InProgress, "\"inProgress\"")]
    [InlineData(TicketStatus.PendingResponse, "\"pendingResponse\"")]
    [InlineData(TicketPriority.Urgent, "\"urgent\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    [Fact]
    public void CarriesEveryCategoryStatusAndPriorityTheClientOffers()
    {
        Assert.Equal(8, Enum.GetValues<TicketCategory>().Length);
        Assert.Equal(6, Enum.GetValues<TicketStatus>().Length);
        Assert.Equal(4, Enum.GetValues<TicketPriority>().Length);
    }

    // The client's mapper folds these older spellings in, so the API has to
    // as well or a migrated row stops reading.
    [Theory]
    [InlineData("IT", TicketCategory.ItSupport)]
    [InlineData("Tech", TicketCategory.ItSupport)]
    [InlineData("HR", TicketCategory.HrPolicy)]
    [InlineData("Finance", TicketCategory.Payroll)]
    [InlineData("Facilities", TicketCategory.Other)]
    [InlineData("it_support", TicketCategory.ItSupport)]
    public void AcceptsTheLegacyCategorySpellings(string input, TicketCategory expected)
    {
        Assert.True(TicketCategories.TryParse(input, out TicketCategory category));
        Assert.Equal(expected, category);
    }

    [Theory]
    [InlineData("critical", TicketPriority.Urgent)]
    [InlineData("normal", TicketPriority.Medium)]
    [InlineData("high", TicketPriority.High)]
    public void AcceptsTheLegacyPrioritySpellings(string input, TicketPriority expected)
    {
        Assert.True(TicketPriorities.TryParse(input, out TicketPriority priority));
        Assert.Equal(expected, priority);
    }

    // ── what the form will not accept ───────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Help")]
    public void RefusesASubjectTooShortToTriage(string subject)
    {
        Assert.Equal(TicketRefusal.SubjectRequired, Tickets.Refusal(Ticket(subject: subject)));
    }

    [Fact]
    public void RefusesASubjectLongerThanTheFieldAllows()
    {
        Assert.Equal(
            TicketRefusal.SubjectTooLong,
            Tickets.Refusal(Ticket(subject: new string('a', Tickets.LongestSubject + 1))));
    }

    [Fact]
    public void AcceptsATicketTheFormWouldAccept()
    {
        Assert.Equal(TicketRefusal.None, Tickets.Refusal(Ticket()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RefusesAnEmptyMessage(string message)
    {
        Assert.Equal(TicketRefusal.MessageRequired, Tickets.MessageRefusal(message));
    }

    // ── the SLA, derived ────────────────────────────────────────────────────

    // The client stores a due date, so reprioritising a ticket leaves the old
    // target in place. These are the intervals its own hardcoded dates imply,
    // measured from the day the ticket was raised.
    [Theory]
    [InlineData(TicketPriority.Urgent, 1)]
    [InlineData(TicketPriority.High, 2)]
    [InlineData(TicketPriority.Medium, 7)]
    [InlineData(TicketPriority.Low, 14)]
    public void TheSlaTargetFollowsThePriority(TicketPriority priority, int days)
    {
        Assert.Equal(
            Raised.AddDays(days),
            Tickets.SlaDueAt(Ticket(priority: priority)));
    }

    // Which is the whole point of deriving it: raising the priority pulls the
    // deadline in, rather than leaving yesterday's.
    [Fact]
    public void ReprioritisingMovesTheDeadline()
    {
        HelpdeskTicket ticket = Ticket(priority: TicketPriority.Low);

        DateTimeOffset was = Tickets.SlaDueAt(ticket);

        ticket.Priority = TicketPriority.Urgent;

        Assert.True(Tickets.SlaDueAt(ticket) < was);
    }

    [Fact]
    public void ATicketPastItsDeadlineIsOverdue()
    {
        Assert.True(Tickets.IsOverdue(
            Ticket(priority: TicketPriority.Urgent), asAt: Raised.AddDays(2)));
    }

    [Fact]
    public void ATicketInsideItsDeadlineIsNotOverdue()
    {
        Assert.False(Tickets.IsOverdue(
            Ticket(priority: TicketPriority.Medium), asAt: Raised.AddDays(2)));
    }

    // Lateness is about whether it is still open. A ticket answered three
    // weeks late is closed, not perpetually breaching.
    [Fact]
    public void AResolvedTicketIsNeverOverdueHoweverLateItWas()
    {
        Assert.False(Tickets.IsOverdue(
            Ticket(
                priority: TicketPriority.Urgent,
                status: TicketStatus.Resolved,
                resolvedAt: Raised.AddDays(30)),
            asAt: Raised.AddDays(60)));
    }

    // Whether the SLA was met is about when it was resolved, not about now.
    [Fact]
    public void TheSlaIsMetWhenItWasResolvedInTime()
    {
        Assert.True(Tickets.MetSla(
            Ticket(
                priority: TicketPriority.Medium,
                status: TicketStatus.Resolved,
                resolvedAt: Raised.AddDays(3))));
    }

    [Fact]
    public void TheSlaIsBreachedWhenItWasResolvedLate()
    {
        Assert.False(Tickets.MetSla(
            Ticket(
                priority: TicketPriority.Medium,
                status: TicketStatus.Resolved,
                resolvedAt: Raised.AddDays(30))));
    }

    // Still open, so there is nothing to judge yet.
    [Fact]
    public void AnOpenTicketHasNoSlaVerdict()
    {
        Assert.Null(Tickets.MetSla(Ticket()));
    }

    // ── resolved and closed ─────────────────────────────────────────────────

    [Theory]
    [InlineData(TicketStatus.Resolved)]
    [InlineData(TicketStatus.Closed)]
    public void ResolvedAndClosedAreBothFinished(TicketStatus status)
    {
        Assert.True(Tickets.IsFinished(status));
    }

    [Theory]
    [InlineData(TicketStatus.Open)]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.Pending)]
    [InlineData(TicketStatus.PendingResponse)]
    public void EverythingElseIsStillRunning(TicketStatus status)
    {
        Assert.False(Tickets.IsFinished(status));
    }

    // Stamped by the move into a finished status and cleared by the move out,
    // so the date and the status cannot disagree.
    [Fact]
    public void ResolvingStampsTheMoment()
    {
        Assert.Equal(
            Raised,
            Tickets.ResolvedAt(TicketStatus.Resolved, was: null, asAt: Raised));
    }

    [Fact]
    public void ReopeningClearsTheResolvedMoment()
    {
        Assert.Null(Tickets.ResolvedAt(
            TicketStatus.InProgress, was: Raised, asAt: Raised.AddDays(1)));
    }

    [Fact]
    public void ClosingAResolvedTicketKeepsTheMomentItWasResolved()
    {
        Assert.Equal(
            Raised,
            Tickets.ResolvedAt(TicketStatus.Closed, was: Raised, asAt: Raised.AddDays(5)));
    }

    // ── who is waiting ──────────────────────────────────────────────────────

    // The client carries both "Pending" and "Pending Response" and nothing
    // says which way round they are. These name it: one waits on us, the other
    // waits on them.
    [Fact]
    public void PendingResponseIsWaitingOnTheEmployee()
    {
        Assert.True(Tickets.IsWaitingOnSubmitter(TicketStatus.PendingResponse));
        Assert.False(Tickets.IsWaitingOnSubmitter(TicketStatus.Pending));
    }

    // ── the reference ───────────────────────────────────────────────────────

    [Fact]
    public void ReferencesReadAsSomethingSomebodyCanQuote()
    {
        Assert.Equal("TKT-2026-00042", Tickets.Reference(2026, 42));
    }

    // ── the queue's figures ─────────────────────────────────────────────────

    private static TicketFacts Facts(
        TicketStatus status,
        bool overdue = false,
        bool? metSla = null,
        decimal? hoursToFirstReply = null) =>
        new()
        {
            Status = status,
            IsOverdue = overdue,
            MetSla = metSla,
            HoursToFirstReply = hoursToFirstReply,
        };

    [Fact]
    public void CountsTheQueueByState()
    {
        TicketTally tally = TicketQueue.Of(
        [
            Facts(TicketStatus.Open),
            Facts(TicketStatus.Open, overdue: true),
            Facts(TicketStatus.InProgress),
            Facts(TicketStatus.PendingResponse),
            Facts(TicketStatus.Resolved, metSla: true),
            Facts(TicketStatus.Closed, metSla: false),
        ]);

        Assert.Equal(6, tally.Total);
        Assert.Equal(2, tally.Open);
        Assert.Equal(1, tally.InProgress);
        Assert.Equal(1, tally.AwaitingEmployee);
        Assert.Equal(1, tally.Resolved);
        Assert.Equal(1, tally.Closed);
        Assert.Equal(1, tally.Overdue);
    }

    // Over the tickets that have actually been settled — an open ticket has
    // not met or missed anything yet, and counting it either way would move
    // the figure for no reason.
    [Fact]
    public void SlaComplianceIsOverSettledTicketsOnly()
    {
        TicketTally tally = TicketQueue.Of(
        [
            Facts(TicketStatus.Resolved, metSla: true),
            Facts(TicketStatus.Resolved, metSla: true),
            Facts(TicketStatus.Resolved, metSla: false),
            Facts(TicketStatus.Open),
        ]);

        Assert.Equal(66.7m, tally.SlaCompliance);
    }

    // Null rather than a hundred per cent, which would read as a perfect
    // record on the day a company starts using the help desk.
    [Fact]
    public void NothingSettledMeansNoComplianceFigureRatherThanAPerfectOne()
    {
        Assert.Null(TicketQueue.Of([Facts(TicketStatus.Open)]).SlaCompliance);
    }

    [Fact]
    public void AveragesTheTimeToAFirstReply()
    {
        TicketTally tally = TicketQueue.Of(
        [
            Facts(TicketStatus.Resolved, metSla: true, hoursToFirstReply: 2m),
            Facts(TicketStatus.Resolved, metSla: true, hoursToFirstReply: 4m),
            Facts(TicketStatus.Open),
        ]);

        Assert.Equal(3m, tally.AverageHoursToFirstReply);
    }

    [Fact]
    public void NoRepliesYetMeansNoAverage()
    {
        Assert.Null(TicketQueue.Of([Facts(TicketStatus.Open)]).AverageHoursToFirstReply);
    }

    [Fact]
    public void AnEmptyQueueCountsNothing()
    {
        TicketTally tally = TicketQueue.Of([]);

        Assert.Equal(0, tally.Total);
        Assert.Null(tally.SlaCompliance);
    }
}
