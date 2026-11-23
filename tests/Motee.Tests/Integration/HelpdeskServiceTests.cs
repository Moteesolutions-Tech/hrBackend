using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Helpdesk;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Helpdesk;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The HR help desk, out of the database.
//
// Two things these prove. An internal note never reaches the person who raised
// the ticket — it is filtered out of the query rather than flagged in the
// payload. And the SLA deadline follows the priority, so reprioritising a
// ticket moves the deadline instead of leaving the one it was given on the day
// it was raised.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class HelpdeskServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _agentId;
    private Guid _employeeId;
    private Guid _strangerId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Owner",
            LastName = "Okafor",
            Email = "owner@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "People", Code = "PPL" }))
            .Department!.Id;

        _agentId = await HireAsync(provider, "Ada");
        _employeeId = await HireAsync(provider, "Bola");
        _strangerId = await HireAsync(provider, "Chidi");

        await AccountAsync(_agentId, "ada");
        await AccountAsync(_employeeId, "bola");
        await AccountAsync(_strangerId, "chidi");

        await ActAsync(provider, _employeeId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IHelpdeskService Desk(ServiceProvider provider) =>
        Resolve<IHelpdeskService>(provider);

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Adviser",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task AccountAsync(Guid employeeId, string handle)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private async Task ActAsync(ServiceProvider provider, Guid employeeId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = await seed.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }

    private static TicketRequest Raising(
        string subject = "March payslip is missing my overtime",
        TicketCategory category = TicketCategory.Payroll,
        TicketPriority priority = TicketPriority.Medium,
        Guid? submitter = null) => new()
        {
            Subject = subject,
            Description = "The overtime I logged on the 14th has not been paid.",
            Category = category,
            Priority = priority,
            SubmitterEmployeeId = submitter,
        };

    private static async Task<TicketDto> RaisedAsync(
        ServiceProvider provider,
        TicketRequest? request = null,
        bool asAgent = false)
    {
        TicketResult result = await Desk(provider).RaiseAsync(
            request ?? Raising(), asAgent);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Ticket!;
    }

    // ── raising one ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewTicketIsOpenWithAQuotableReference()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Equal("TKT-2026-00001", ticket.Reference);
        Assert.Equal(_employeeId, ticket.SubmitterEmployeeId);
        Assert.Null(ticket.AssigneeEmployeeId);
        Assert.Null(ticket.ResolvedAt);

        // Still open, so there is nothing to judge against the SLA yet.
        Assert.Null(ticket.MetSla);
        Assert.Null(ticket.FirstReplyAt);
    }

    // The opening line is the first message, so the conversation reads in one
    // place rather than the description living on the ticket and every reply
    // living somewhere else.
    [SkippableFact]
    public async Task TheDescriptionIsTheFirstMessage()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        TicketMessageDto first = Assert.Single(ticket.Messages);

        Assert.Equal(ticket.Description, first.Body);
        Assert.False(first.IsFromAgent);
        Assert.Equal("Bola Okafor", first.AuthorName);
    }

    [SkippableFact]
    public async Task NumbersEachTicketInSequence()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RaisedAsync(provider);

        Assert.Equal("TKT-2026-00002", (await RaisedAsync(provider)).Reference);
    }

    [SkippableFact]
    public async Task RefusesASubjectTooShortToTriage()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            TicketRefusal.SubjectRequired,
            (await Desk(provider).RaiseAsync(Raising(subject: "Help"), asAgent: false)).Refusal);
    }

    // Recording a ticket for somebody else is an agent's job.
    [SkippableFact]
    public async Task AnEmployeeCannotRaiseATicketForSomebodyElse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            TicketOutcome.Forbidden,
            (await Desk(provider).RaiseAsync(
                Raising(submitter: _strangerId), asAgent: false)).Outcome);
    }

    [SkippableFact]
    public async Task AnAgentRecordsATicketOnSomebodysBehalf()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ActAsync(provider, _agentId);

        TicketDto ticket = await RaisedAsync(
            provider, Raising(submitter: _strangerId), asAgent: true);

        Assert.Equal(_strangerId, ticket.SubmitterEmployeeId);
        Assert.Equal("Chidi Okafor", ticket.SubmitterName);
    }

    // ── the SLA, derived ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheDeadlineFollowsThePriority()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto medium = await RaisedAsync(provider, Raising(priority: TicketPriority.Medium));

        // Seven days from the moment it was raised.
        Assert.Equal(_clock.GetUtcNow().AddDays(7), medium.SlaDueAt);
    }

    // The whole reason it is derived: the client stores the due date, so
    // reprioritising leaves yesterday's deadline in place.
    [SkippableFact]
    public async Task ReprioritisingMovesTheDeadline()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider, Raising(priority: TicketPriority.Low));

        DateTimeOffset was = ticket.SlaDueAt;

        TicketResult raised = await Desk(provider)
            .SetPriorityAsync(ticket.Id, TicketPriority.Urgent);

        Assert.True(raised.Ticket!.SlaDueAt < was);
        Assert.Equal(_clock.GetUtcNow().AddDays(1), raised.Ticket.SlaDueAt);
    }

    [SkippableFact]
    public async Task ATicketPastItsDeadlineIsOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider, Raising(priority: TicketPriority.Urgent));

        Assert.False(ticket.IsOverdue);

        _clock.Advance(TimeSpan.FromDays(2));

        Assert.True((await Desk(provider).GetAsync(ticket.Id, asAgent: true))!.IsOverdue);
    }

    // A ticket answered three weeks late is closed, not perpetually breaching.
    [SkippableFact]
    public async Task AResolvedTicketIsNoLongerOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider, Raising(priority: TicketPriority.Urgent));

        _clock.Advance(TimeSpan.FromDays(10));

        TicketResult resolved = await Desk(provider)
            .SetStatusAsync(ticket.Id, TicketStatus.Resolved);

        Assert.False(resolved.Ticket!.IsOverdue);

        // And the SLA verdict is judged on when it was settled, so it is a
        // breach — just not an ongoing one.
        Assert.False(resolved.Ticket.MetSla);
    }

    [SkippableFact]
    public async Task TheSlaIsMetWhenItWasResolvedInTime()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider, Raising(priority: TicketPriority.Medium));

        _clock.Advance(TimeSpan.FromDays(2));

        TicketResult resolved = await Desk(provider)
            .SetStatusAsync(ticket.Id, TicketStatus.Resolved);

        Assert.True(resolved.Ticket!.MetSla);
        Assert.Equal(_clock.GetUtcNow(), resolved.Ticket.ResolvedAt);
    }

    [SkippableFact]
    public async Task ReopeningClearsTheResolvedMoment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        await Desk(provider).SetStatusAsync(ticket.Id, TicketStatus.Resolved);

        TicketResult reopened = await Desk(provider)
            .SetStatusAsync(ticket.Id, TicketStatus.InProgress);

        Assert.Null(reopened.Ticket!.ResolvedAt);
        Assert.Null(reopened.Ticket.MetSla);
    }

    // ── internal notes ──────────────────────────────────────────────────────

    // The rule this module exists to get right.
    [SkippableFact]
    public async Task AnInternalNoteNeverReachesThePersonWhoRaisedTheTicket()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        await ActAsync(provider, _agentId);

        await Desk(provider).ReplyAsync(
            ticket.Id,
            new TicketReplyRequest { Body = "Payroll confirmed the error.", IsInternalNote = true },
            asAgent: true);

        await Desk(provider).ReplyAsync(
            ticket.Id,
            new TicketReplyRequest { Body = "We are looking into this." },
            asAgent: true);

        // The agent sees three: the opening message, the note, the reply.
        Assert.Equal(3, (await Desk(provider).GetAsync(ticket.Id, asAgent: true))!.Messages.Count);

        await ActAsync(provider, _employeeId);

        IReadOnlyList<TicketMessageDto> theirs =
            (await Desk(provider).GetAsync(ticket.Id, asAgent: false))!.Messages;

        Assert.Equal(2, theirs.Count);
        Assert.DoesNotContain(theirs, message => message.IsInternalNote);
        Assert.DoesNotContain(theirs, message => message.Body.Contains("Payroll confirmed"));
    }

    [SkippableFact]
    public async Task AnEmployeeCannotLeaveAnInternalNote()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        Assert.Equal(
            TicketOutcome.Forbidden,
            (await Desk(provider).ReplyAsync(
                ticket.Id,
                new TicketReplyRequest { Body = "Secret", IsInternalNote = true },
                asAgent: false)).Outcome);
    }

    // Decided by the grant the author held when they wrote it, rather than by
    // a string match on their department's name.
    [SkippableFact]
    public async Task AnAgentsReplyIsMarkedAsComingFromTheHelpDesk()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        await ActAsync(provider, _agentId);

        TicketResult replied = await Desk(provider).ReplyAsync(
            ticket.Id,
            new TicketReplyRequest { Body = "We are looking into this." },
            asAgent: true);

        TicketMessageDto reply = replied.Ticket!.Messages[^1];

        Assert.True(reply.IsFromAgent);
        Assert.Equal("Ada Okafor", reply.AuthorName);
    }

    [SkippableFact]
    public async Task AStrangerCannotReadOrReplyToSomebodyElsesTicket()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        await ActAsync(provider, _strangerId);

        Assert.Null(await Desk(provider).GetAsync(ticket.Id, asAgent: false));

        Assert.Equal(
            TicketRefusal.NotYours,
            (await Desk(provider).ReplyAsync(
                ticket.Id,
                new TicketReplyRequest { Body = "Nosy" },
                asAgent: false)).Refusal);
    }

    [SkippableFact]
    public async Task AClosedTicketTakesNoMoreReplies()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        await Desk(provider).SetStatusAsync(ticket.Id, TicketStatus.Closed);

        Assert.Equal(
            TicketRefusal.AlreadyClosed,
            (await Desk(provider).ReplyAsync(
                ticket.Id,
                new TicketReplyRequest { Body = "One more thing" },
                asAgent: false)).Refusal);
    }

    // ── whose turn it is ────────────────────────────────────────────────────

    // Leaving it waiting on them after they have answered is how a ticket
    // sits in nobody's queue.
    [SkippableFact]
    public async Task TheEmployeeAnsweringPutsTheTicketBackWithTheDesk()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        await ActAsync(provider, _agentId);

        await Desk(provider).ReplyAsync(
            ticket.Id,
            new TicketReplyRequest
            {
                Body = "Could you send the screenshot?",
                Status = TicketStatus.PendingResponse,
            },
            asAgent: true);

        await ActAsync(provider, _employeeId);

        TicketResult answered = await Desk(provider).ReplyAsync(
            ticket.Id,
            new TicketReplyRequest { Body = "Attached." },
            asAgent: false);

        Assert.Equal(TicketStatus.InProgress, answered.Ticket!.Status);
    }

    // ── the first reply ─────────────────────────────────────────────────────

    // An internal note is not an answer to the person waiting.
    [SkippableFact]
    public async Task AnInternalNoteIsNotAFirstReply()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        await ActAsync(provider, _agentId);

        _clock.Advance(TimeSpan.FromHours(1));

        await Desk(provider).ReplyAsync(
            ticket.Id,
            new TicketReplyRequest { Body = "Checking with payroll.", IsInternalNote = true },
            asAgent: true);

        Assert.Null((await Desk(provider).GetAsync(ticket.Id, asAgent: true))!.FirstReplyAt);

        _clock.Advance(TimeSpan.FromHours(2));

        TicketResult replied = await Desk(provider).ReplyAsync(
            ticket.Id,
            new TicketReplyRequest { Body = "We are on it." },
            asAgent: true);

        Assert.Equal(3m, replied.Ticket!.HoursToFirstReply);
    }

    // ── assigning ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task PickingATicketUpTakesItOutOfTheOpenQueue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        TicketResult assigned = await Desk(provider).AssignAsync(ticket.Id, _agentId);

        Assert.Equal(_agentId, assigned.Ticket!.AssigneeEmployeeId);
        Assert.Equal("Ada Okafor", assigned.Ticket.AssigneeName);
        Assert.Equal(TicketStatus.InProgress, assigned.Ticket.Status);
    }

    [SkippableFact]
    public async Task UnassigningReturnsItToTheQueue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        await Desk(provider).AssignAsync(ticket.Id, _agentId);

        Assert.Null((await Desk(provider).AssignAsync(ticket.Id, null)).Ticket!.AssigneeEmployeeId);
    }

    [SkippableFact]
    public async Task RefusesAnAssigneeWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        Assert.Equal(
            TicketOutcome.EmployeeNotFound,
            (await Desk(provider).AssignAsync(ticket.Id, Guid.NewGuid())).Outcome);
    }

    // ── the queue ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ListsTheOpenQueueWorstFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RaisedAsync(provider, Raising(subject: "A low priority question",
            priority: TicketPriority.Low));

        await RaisedAsync(provider, Raising(subject: "Payroll is wrong this month",
            priority: TicketPriority.Urgent));

        PagedResult<TicketDto> queue = await Desk(provider)
            .ListAsync(new TicketQuery { OpenOnly = true });

        Assert.Equal(TicketPriority.Urgent, queue.Items[0].Priority);
    }

    [SkippableFact]
    public async Task FiltersToWhatNobodyHasPickedUp()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto taken = await RaisedAsync(provider);
        TicketDto waiting = await RaisedAsync(
            provider, Raising(subject: "Nobody has looked at this"));

        await Desk(provider).AssignAsync(taken.Id, _agentId);

        PagedResult<TicketDto> unassigned = await Desk(provider)
            .ListAsync(new TicketQuery { UnassignedOnly = true });

        Assert.Equal([waiting.Id], unassigned.Items.Select(row => row.Id));
    }

    [SkippableFact]
    public async Task FiltersToWhatIsOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto urgent = await RaisedAsync(
            provider, Raising(subject: "Urgent and ageing", priority: TicketPriority.Urgent));

        await RaisedAsync(provider, Raising(subject: "A low priority question",
            priority: TicketPriority.Low));

        _clock.Advance(TimeSpan.FromDays(2));

        PagedResult<TicketDto> overdue = await Desk(provider)
            .ListAsync(new TicketQuery { OverdueOnly = true });

        Assert.Equal([urgent.Id], overdue.Items.Select(row => row.Id));
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RaisedAsync(provider, Raising(subject: "Bonus was 100% wrong"));
        await RaisedAsync(provider, Raising(subject: "Something else entirely"));

        Assert.Single(
            (await Desk(provider).ListAsync(new TicketQuery { Search = "100%" })).Items);
    }

    [SkippableFact]
    public async Task AnEmployeeSeesOnlyTheirOwnTickets()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RaisedAsync(provider);

        await ActAsync(provider, _strangerId);
        await RaisedAsync(provider, Raising(subject: "A different problem entirely"));

        Assert.Single(await Desk(provider).MineAsync());
    }

    // ── the figures ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsTheQueuesFigures()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto met = await RaisedAsync(provider, Raising(priority: TicketPriority.Medium));
        TicketDto missed = await RaisedAsync(
            provider, Raising(subject: "This one went late", priority: TicketPriority.Urgent));

        await RaisedAsync(provider, Raising(subject: "Still waiting on us"));

        _clock.Advance(TimeSpan.FromDays(2));

        await Desk(provider).SetStatusAsync(met.Id, TicketStatus.Resolved);

        _clock.Advance(TimeSpan.FromDays(10));

        await Desk(provider).SetStatusAsync(missed.Id, TicketStatus.Resolved);

        TicketTally tally = await Desk(provider).SummaryAsync();

        Assert.Equal(3, tally.Total);
        Assert.Equal(2, tally.Resolved);

        // One of the two settled tickets met its deadline.
        Assert.Equal(50m, tally.SlaCompliance);
    }

    // Not a hundred per cent, which would show a perfect record on the day a
    // company starts using the help desk.
    [SkippableFact]
    public async Task NothingSettledMeansNoComplianceFigure()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RaisedAsync(provider);

        Assert.Null((await Desk(provider).SummaryAsync()).SlaCompliance);
    }

    // ── deleting ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task DeletingATicketTakesItsConversationWithIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TicketDto ticket = await RaisedAsync(provider);

        Assert.True((await Desk(provider).DeleteAsync(ticket.Id)).Succeeded);

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Empty(await read.TicketMessages.ToListAsync());
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersQueue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RaisedAsync(provider);

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ola",
            LastName = "Bello",
            Email = "ola@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(other.Succeeded, string.Join("; ", other.Errors));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        Assert.Empty((await Desk(provider).ListAsync(new TicketQuery())).Items);
        Assert.Equal(0, (await Desk(provider).SummaryAsync()).Total);
    }
}
