using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Leave;
using Motee.Domain.Approvals;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Booking time off: the policy rules that decide whether it can be booked, and the way a
// request's days stop being reserved once somebody refuses it.
[Collection(PostgresCollection.Name)]
public class LeaveRequestTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _annualTypeId;
    private Guid _sickTypeId;

    private async Task<ServiceProvider> ArrangeAsync(decimal annualDays = 25m)
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "GB",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            _annualTypeId = await seed.LeaveTypes
                .Where(type => type.Code == "annual").Select(type => type.Id).FirstAsync();

            _sickTypeId = await seed.LeaveTypes
                .Where(type => type.Code == "sick").Select(type => type.Id).FirstAsync();

            // The seeded UK figure is 28; tests want a round number they can do sums
            // against without the statutory value quietly changing what they mean.
            LeavePolicy annual = await seed.LeavePolicies
                .FirstAsync(policy => policy.LeaveTypeId == _annualTypeId);

            annual.DaysPerYear = annualDays;
            annual.AccruesMonthly = false;

            // The seeded UK calendar is real, so whether a test date lands on a bank
            // holiday depends on when the suite is run — a Summer Bank Holiday Monday
            // would silently change what a range costs. Cleared here so the arithmetic
            // below is deterministic; tests that care add a holiday explicitly, and the
            // seeding itself is covered by LeaveSeedTests.
            seed.PublicHolidays.RemoveRange(seed.PublicHolidays);

            _employeeId = Guid.NewGuid();

            seed.Employees.Add(new Employee
            {
                Id = _employeeId,
                TenantId = _tenantId,
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
                Status = EmployeeStatus.Active,
                StartDate = new DateOnly(2020, 1, 1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    // Far enough out to clear the 14-day notice the seeded annual policy asks for.
    private static DateOnly SoonEnough(int addDays = 30) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(addDays));

    private Task<LeaveRequestResult> BookAsync(
        ServiceProvider provider,
        DateOnly start,
        DateOnly end,
        Guid? typeId = null,
        bool halfDay = false) =>
        Resolve<ILeaveRequestService>(provider).SubmitAsync(new LeaveRequestSubmission
        {
            EmployeeId = _employeeId,
            LeaveTypeId = typeId ?? _annualTypeId,
            StartDate = start,
            EndDate = end,
            IsHalfDay = halfDay,
            Reason = "Holiday",
        });

    // No leave workflow is configured by default, so this also pins the fallback: a
    // company that has not set one up is not a company where leave is impossible.
    [SkippableFact]
    public async Task LeaveCanBeBookedAndCostsWorkingDaysOnly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // A Monday, through the following Friday: twelve calendar days, ten working.
        DateOnly monday = NextMonday(SoonEnough());

        LeaveRequestResult booked = await BookAsync(provider, monday, monday.AddDays(11));

        Assert.True(booked.Succeeded, $"{booked.Outcome}: {booked.Reason}");
        Assert.Equal(10m, booked.Request!.TotalDays);
        Assert.Equal(LeaveRequestStatus.Approved, booked.Request.Status);
    }

    // The frontend charges people for bank holidays. This is the case that proves the
    // backend does not.
    [SkippableFact]
    public async Task PublicHolidaysInsideTheRangeAreNotCharged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly monday = NextMonday(SoonEnough());

        // Put a company holiday in the middle of the week they are away.
        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.PublicHolidays.Add(new PublicHoliday
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                Date = monday.AddDays(2),
                Name = "Founders' Day",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        LeaveRequestResult booked = await BookAsync(provider, monday, monday.AddDays(4));

        // Monday to Friday is five working days, less the one the office is shut.
        Assert.True(booked.Succeeded, $"{booked.Outcome}: {booked.Reason}");
        Assert.Equal(4m, booked.Request!.TotalDays);
    }

    [SkippableFact]
    public async Task AHalfDayCostsHalf()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly monday = NextMonday(SoonEnough());

        LeaveRequestResult booked = await BookAsync(provider, monday, monday, halfDay: true);

        Assert.True(booked.Succeeded, $"{booked.Outcome}: {booked.Reason}");
        Assert.Equal(0.5m, booked.Request!.TotalDays);
    }

    // The seeded annual policy asks for fourteen days' notice.
    [SkippableFact]
    public async Task LeaveBookedAtTooShortNoticeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly soon = NextMonday(SoonEnough(3));

        LeaveRequestResult booked = await BookAsync(provider, soon, soon);

        Assert.Equal(LeaveRequestOutcome.InsufficientNotice, booked.Outcome);

        // The message says the numbers, because "insufficient notice" alone leaves
        // somebody guessing how much would have been enough.
        Assert.Contains("14 days", booked.Reason!, StringComparison.Ordinal);
    }

    // Nobody gives notice of falling ill, and recording last week's absence is ordinary.
    [SkippableFact]
    public async Task SickLeaveCanBeRecordedAfterTheFact()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly lastWeek = NextMonday(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)));

        LeaveRequestResult booked = await BookAsync(
            provider, lastWeek, lastWeek, typeId: _sickTypeId);

        Assert.True(booked.Succeeded, $"{booked.Outcome}: {booked.Reason}");
    }

    [SkippableFact]
    public async Task MoreDaysThanAreLeftIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 5m);

        DateOnly monday = NextMonday(SoonEnough());

        LeaveRequestResult booked = await BookAsync(provider, monday, monday.AddDays(11));

        Assert.Equal(LeaveRequestOutcome.InsufficientBalance, booked.Outcome);
        Assert.Contains("5", booked.Reason!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task DaysAlreadyBookedCountAgainstTheNextRequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 10m);

        DateOnly monday = NextMonday(SoonEnough());

        Assert.True((await BookAsync(provider, monday, monday.AddDays(4))).Succeeded);

        // Five taken, five left. Monday to the Monday a week later is six working days.
        LeaveRequestResult second = await BookAsync(
            provider, monday.AddDays(14), monday.AddDays(21));

        Assert.Equal(LeaveRequestOutcome.InsufficientBalance, second.Outcome);
    }

    // The claim that pending days are reserved, tested against a chain that actually
    // holds a request open. With no workflow configured a request is approved on
    // submission, so the test above cannot tell the two states apart — and "pending days
    // reserve" is the half that stops somebody booking the same days twice while an
    // approver is still thinking.
    [SkippableFact]
    public async Task DaysAwaitingADecisionAreReserved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 10m);

        await ChainAsync(provider);

        DateOnly monday = NextMonday(SoonEnough());

        LeaveRequestResult first = await BookAsync(provider, monday, monday.AddDays(4));

        Assert.True(first.Succeeded, $"{first.Outcome}: {first.Reason}");
        Assert.Equal(LeaveRequestStatus.Pending, first.Request!.Status);

        Assert.Equal(
            5m,
            (await Resolve<ILeaveBalanceService>(provider)
                .ForAsync(_employeeId, _annualTypeId, monday))!.Pending);

        LeaveRequestResult second = await BookAsync(
            provider, monday.AddDays(14), monday.AddDays(21));

        Assert.Equal(LeaveRequestOutcome.InsufficientBalance, second.Outcome);
    }

    // Refusing a request has to give the days back, and the engine is what tells the
    // leave module that somebody refused it.
    [SkippableFact]
    public async Task RejectingARequestReleasesItsDays()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 10m);

        Guid managerUserId = await ChainAsync(provider);

        DateOnly monday = NextMonday(SoonEnough());

        LeaveRequestResult booked = await BookAsync(provider, monday, monday.AddDays(4));
        Assert.Equal(LeaveRequestStatus.Pending, booked.Request!.Status);

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            managerUserId.ToString();

        ApprovalResult decided = await Resolve<IApprovalService>(provider)
            .DecideAsync(booked.Request.Approval!.Id, ApprovalStepStatus.Rejected, "No cover");

        Assert.True(decided.Succeeded, decided.Outcome.ToString());

        LeaveRequestDto? after = await Resolve<ILeaveRequestService>(provider)
            .GetAsync(booked.Request.Id);

        Assert.Equal(LeaveRequestStatus.Rejected, after!.Status);

        // The whole point: a refusal must not leave days reserved for ever.
        Assert.Equal(
            10m,
            (await Resolve<ILeaveBalanceService>(provider)
                .ForAsync(_employeeId, _annualTypeId, monday))!.Available);
    }

    // A leave chain with a line manager who can actually act. Returns their user id so a
    // test can decide as them.
    private async Task<Guid> ChainAsync(ServiceProvider provider)
    {
        Guid managerId = Guid.NewGuid();
        Guid managerUserId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Employees.Add(new Employee
            {
                Id = managerId,
                TenantId = _tenantId,
                FirstName = "Cara",
                LastName = "Nwosu",
                Email = "cara@acme.com",
                Status = EmployeeStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
            {
                Id = managerUserId,
                TenantId = _tenantId,
                EmployeeId = managerId,
                FirstName = "Cara",
                LastName = "Nwosu",
                Email = "cara@acme.com",
                NormalizedEmail = "CARA@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            Employee subject = await seed.Employees.FirstAsync(e => e.Id == _employeeId);
            subject.ManagerId = managerId;

            await seed.SaveChangesAsync();
        }

        ApprovalTemplateResult created = await Resolve<IApprovalTemplateService>(provider)
            .CreateAsync(new ApprovalTemplateRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                Name = "Leave approval",
                IsDefault = true,
                Steps =
                [
                    new ApprovalTemplateStepRequest
                    {
                        Label = "Manager review",
                        Approver = ApproverResolver.LineManager,
                        Required = true,
                    },
                ],
            });

        Assert.True(created.Succeeded, created.Outcome.ToString());

        return managerUserId;
    }

    [SkippableFact]
    public async Task LeaveOverlappingExistingLeaveIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly monday = NextMonday(SoonEnough());

        Assert.True((await BookAsync(provider, monday, monday.AddDays(4))).Succeeded);

        LeaveRequestResult clash = await BookAsync(
            provider, monday.AddDays(2), monday.AddDays(6));

        Assert.Equal(LeaveRequestOutcome.Overlaps, clash.Outcome);
    }

    [SkippableFact]
    public async Task ARangeOfOnlyWeekendsIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly saturday = NextMonday(SoonEnough()).AddDays(-2);

        LeaveRequestResult booked = await BookAsync(provider, saturday, saturday.AddDays(1));

        Assert.Equal(LeaveRequestOutcome.NoWorkingDays, booked.Outcome);
    }

    [SkippableFact]
    public async Task AHalfDayAcrossARangeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly monday = NextMonday(SoonEnough());

        LeaveRequestResult booked = await BookAsync(
            provider, monday, monday.AddDays(4), halfDay: true);

        Assert.Equal(LeaveRequestOutcome.InvalidDates, booked.Outcome);
    }

    // Cancelling gives the days back. Without it somebody's balance is permanently down
    // for a holiday they never took.
    [SkippableFact]
    public async Task CancellingReturnsTheDays()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 10m);

        DateOnly monday = NextMonday(SoonEnough());

        LeaveRequestResult booked = await BookAsync(provider, monday, monday.AddDays(4));
        Assert.True(booked.Succeeded);

        ILeaveBalanceService balances = Resolve<ILeaveBalanceService>(provider);

        Assert.Equal(
            5m,
            (await balances.ForAsync(_employeeId, _annualTypeId, monday))!.Available);

        LeaveRequestResult cancelled = await Resolve<ILeaveRequestService>(provider)
            .CancelAsync(booked.Request!.Id, "Trip called off");

        Assert.True(cancelled.Succeeded, cancelled.Outcome.ToString());
        Assert.Equal(LeaveRequestStatus.Cancelled, cancelled.Request!.Status);

        Assert.Equal(
            10m,
            (await balances.ForAsync(_employeeId, _annualTypeId, monday))!.Available);
    }

    // The quote is what the form shows while somebody is still picking dates. It has to
    // give the same answer the submission will, or the form lies.
    [SkippableFact]
    public async Task TheQuoteAgreesWithWhatSubmittingWouldDo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 10m);

        DateOnly monday = NextMonday(SoonEnough());

        ILeaveRequestService leave = Resolve<ILeaveRequestService>(provider);

        LeaveQuoteDto quote = await leave.QuoteAsync(new LeaveQuoteRequest
        {
            EmployeeId = _employeeId,
            LeaveTypeId = _annualTypeId,
            StartDate = monday,
            EndDate = monday.AddDays(4),
        });

        Assert.Empty(quote.Problems);
        Assert.Equal(5m, quote.TotalDays);
        Assert.Equal(10m, quote.AvailableBefore);
        Assert.Equal(5m, quote.AvailableAfter);

        LeaveRequestResult booked = await BookAsync(provider, monday, monday.AddDays(4));

        Assert.True(booked.Succeeded);
        Assert.Equal(quote.TotalDays, booked.Request!.TotalDays);
    }

    // Why ten calendar days cost six. Listed rather than summarised, because "4 days
    // excluded" invites somebody to check and find nothing to check against.
    [SkippableFact]
    public async Task TheQuoteExplainsTheDaysThatCostNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly monday = NextMonday(SoonEnough());

        LeaveQuoteDto quote = await Resolve<ILeaveRequestService>(provider)
            .QuoteAsync(new LeaveQuoteRequest
            {
                EmployeeId = _employeeId,
                LeaveTypeId = _annualTypeId,
                StartDate = monday,
                EndDate = monday.AddDays(11),
            });

        // One weekend falls inside a twelve-day range.
        Assert.Equal(2, quote.NonWorkingDays.Count);
        Assert.All(quote.NonWorkingDays, day => Assert.Equal("Weekend", day.Reason));
    }

    private static DateOnly NextMonday(DateOnly from)
    {
        DateOnly day = from;

        while (day.DayOfWeek != DayOfWeek.Monday)
        {
            day = day.AddDays(1);
        }

        return day;
    }
}
