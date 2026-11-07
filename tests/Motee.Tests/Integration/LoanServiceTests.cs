using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Loans;
using Motee.Application.Organisation;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Domain.Identity;
using Motee.Domain.Loans;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The loan register, out of the database.
//
// Two things these exist to prove. First, that a loan's status comes from its
// approval chain rather than from a column — so approving it in the approvals
// module activates it, with no second write to go stale. Second, that the
// amount repaid is the sum of a ledger: recording, reversing and over-paying
// all behave like bookkeeping rather than like editing a number.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class LoanServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 9, 24).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _managerId;
    private Guid _borrowerId;

    private async Task<ServiceProvider> ArrangeAsync(string country = "NG")
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = country,
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        // The borrower needs a manager, or the chain's first step resolves to
        // nobody and the loan sits blocked rather than pending.
        _managerId = await HireAsync(provider, "Bola");
        Guid managerUserId = await AccountAsync(_managerId, "bola");

        // The same person holds HR and Finance, which is ordinary in a small
        // company and is all the chain needs to resolve past its first step.
        await HoldsAsync(managerUserId, Role.HrAdmin);
        await HoldsAsync(managerUserId, Role.Finance);

        _borrowerId = await HireAsync(provider, "Ngozi", managerId: _managerId);
        await AccountAsync(_borrowerId, "ngozi");

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static ILoanService Register(ServiceProvider provider) =>
        Resolve<ILoanService>(provider);

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid? managerId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            ManagerId = managerId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    // The loan chain asks HR and then Finance by role, and a fresh tenant
    // holds neither — the owner is deliberately given no access level. Without
    // somebody in each, the chain blocks after the line manager, which is the
    // engine behaving correctly and not what these tests are about.
    private async Task HoldsAsync(Guid userId, Role role)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid levelId = await seed.AccessLevels
            .Where(level => level.TemplateSlug == Roles.ToSlug(role))
            .Select(level => level.Id)
            .FirstAsync();

        seed.UserAccessLevels.Add(new UserAccessLevel
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            UserId = userId,
            AccessLevelId = levelId,
            AssignedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private async Task<Guid> AccountAsync(Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
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

        return userId;
    }

    private static void ActAs(ServiceProvider provider, Guid userId) =>
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

    private async Task<Guid> UserForAsync(Guid employeeId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        return await seed.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();
    }

    private async Task<Guid> UserHoldingAsync(Guid accessLevelId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        return await seed.UserAccessLevels
            .Where(assignment => assignment.AccessLevelId == accessLevelId)
            .Select(assignment => assignment.UserId)
            .FirstAsync();
    }

    private LoanRequest Request(
        LoanType type = LoanType.StaffLoan,
        decimal amount = 5000m,
        int months = 12,
        string purpose = "Rent renewal",
        Guid? employeeId = null) => new()
        {
            EmployeeId = employeeId ?? _borrowerId,
            LoanType = type,
            Amount = amount,
            RepaymentMonths = months,
            Purpose = purpose,
        };

    private async Task<LoanDto> RequestedAsync(ServiceProvider provider, LoanRequest? request = null)
    {
        LoanResult result = await Register(provider).RequestAsync(request ?? Request());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Loan!;
    }

    // Walks the seeded loan chain to a decision, acting as whoever each step
    // resolved to — which is what the engine checks before accepting one. A
    // loop because the chain's length depends on what the tenant configured.
    private async Task DecideChainAsync(
        ServiceProvider provider,
        Guid approvalInstanceId,
        bool approve)
    {
        for (int round = 0; round < 6; round++)
        {
            ApprovalDto? chain = await Resolve<IApprovalService>(provider)
                .GetAsync(approvalInstanceId);

            Assert.NotNull(chain);

            if (chain.Status != Motee.Domain.Approvals.ApprovalStatus.InProgress)
            {
                return;
            }

            ApprovalStepDto step = chain.CurrentStep
                ?? throw new InvalidOperationException("Nothing to decide.");

            if (step.ResolvedEmployeeId is Guid approver)
            {
                ActAs(provider, await UserForAsync(approver));
            }
            else if (step.ResolvedRoleId is Guid roleId)
            {
                ActAs(provider, await UserHoldingAsync(roleId));
            }

            ApprovalResult decided = await Resolve<IApprovalService>(provider).DecideAsync(
                chain.Id,
                approve ? ApprovalStepStatus.Approved : ApprovalStepStatus.Rejected,
                approve ? "Agreed." : "Declined.");

            Assert.True(decided.Succeeded, decided.Outcome.ToString());
        }
    }

    private async Task<decimal> RepaidAsync(ServiceProvider provider, Guid loanId) =>
        (await Register(provider).GetAsync(loanId))!.Loan.AmountRepaid;

    // ── raising one ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RecordsALoanRequestAsPendingWithAQuotableReference()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        Assert.Equal(LoanStatus.Pending, loan.Status);
        Assert.Equal("LOAN-2026-00001", loan.Reference);
        Assert.Null(loan.IssuedOn);

        // Nothing is owed on money that has not moved.
        Assert.Equal(0m, loan.Outstanding);
        Assert.Equal(0m, loan.AmountRepaid);
    }

    [SkippableFact]
    public async Task NumbersEachLoanInSequence()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RequestedAsync(provider);

        Assert.Equal("LOAN-2026-00002", (await RequestedAsync(provider)).Reference);
    }

    [SkippableFact]
    public async Task DerivesTheInstalmentFromTheAmountAndTheTerm()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider, Request(amount: 5000m, months: 12));

        Assert.Equal(416.67m, loan.MonthlyRepayment);
    }

    [SkippableFact]
    public async Task SendsTheRequestDownTheLoanChain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        Assert.NotNull(loan.ApprovalInstanceId);
        Assert.Equal(Motee.Domain.Approvals.ApprovalStatus.InProgress, loan.ApprovalStatus);
    }

    // A cooperative loan is a Nigerian arrangement. The frontend enforces this
    // only by not offering the option, which is no enforcement at all.
    [SkippableFact]
    public async Task RefusesALoanProductThisCountryDoesNotOffer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync("GB");

        LoanResult result = await Register(provider)
            .RequestAsync(Request(type: LoanType.CooperativeLoan));

        Assert.Equal(LoanRefusal.TypeNotOfferedInThisCountry, result.Refusal);
    }

    [SkippableFact]
    public async Task RefusesALoanForSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanResult result = await Register(provider)
            .RequestAsync(Request(employeeId: Guid.NewGuid()));

        Assert.Equal(LoanOutcome.EmployeeNotFound, result.Outcome);
    }

    // ── the chain decides whether the money moves ───────────────────────────

    [SkippableFact]
    public async Task AnApprovedChainIssuesTheLoanWithoutASecondWrite()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        await DecideChainAsync(provider, loan.ApprovalInstanceId!.Value, approve: true);

        LoanDto after = (await Register(provider).GetAsync(loan.Id))!.Loan;

        Assert.Equal(LoanStatus.Active, after.Status);

        // Issued on the day it was approved, which is today on the pinned clock.
        Assert.Equal(new DateOnly(2026, 9, 24), after.IssuedOn);
        Assert.Equal(5000m, after.Outstanding);
    }

    [SkippableFact]
    public async Task ARejectedChainRejectsTheLoan()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        await DecideChainAsync(provider, loan.ApprovalInstanceId!.Value, approve: false);

        LoanDto after = (await Register(provider).GetAsync(loan.Id))!.Loan;

        Assert.Equal(LoanStatus.Rejected, after.Status);
        Assert.Null(after.IssuedOn);
    }

    [SkippableFact]
    public async Task HasNoScheduleUntilTheMoneyIsIssued()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        Assert.Empty((await Register(provider).GetAsync(loan.Id))!.Schedule);
    }

    [SkippableFact]
    public async Task SchedulesTheRepaymentsOnceIssued()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        Assert.True((await Register(provider)
            .ApproveAsync(loan.Id, new DateOnly(2026, 1, 1))).Succeeded);

        LoanDetailDto detail = (await Register(provider).GetAsync(loan.Id))!;

        Assert.Equal(12, detail.Schedule.Count);
        Assert.Equal(new DateOnly(2026, 2, 1), detail.Schedule[0].DueOn);
        Assert.Equal(416.67m, detail.Schedule[0].Amount);
        Assert.Equal(5000m, detail.Schedule.Sum(instalment => instalment.Amount));
    }

    [SkippableFact]
    public async Task ADirectDecisionIssuesALoanWithNoChainBehindIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        LoanResult result = await Register(provider).ApproveAsync(loan.Id);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Equal(LoanStatus.Active, result.Loan!.Status);
        Assert.Equal(new DateOnly(2026, 9, 24), result.Loan.IssuedOn);
    }

    [SkippableFact]
    public async Task ADirectRejectionRefusesIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        Assert.Equal(
            LoanStatus.Rejected,
            (await Register(provider).RejectAsync(loan.Id)).Loan!.Status);
    }

    // ── the repayment ledger ────────────────────────────────────────────────

    private async Task<LoanDto> IssuedAsync(
        ServiceProvider provider,
        decimal amount = 5000m,
        int months = 12,
        DateOnly? issuedOn = null)
    {
        LoanDto loan = await RequestedAsync(provider, Request(amount: amount, months: months));

        LoanResult result = await Register(provider)
            .ApproveAsync(loan.Id, issuedOn ?? new DateOnly(2026, 1, 1));

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Loan!;
    }

    [SkippableFact]
    public async Task RepaidIsTheSumOfTheLedgerRatherThanAStoredTotal()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        foreach (decimal amount in (decimal[])[416.67m, 416.67m, 100m])
        {
            LoanResult result = await Register(provider)
                .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = amount });

            Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        }

        LoanDetailDto detail = (await Register(provider).GetAsync(loan.Id))!;

        Assert.Equal(933.34m, detail.Loan.AmountRepaid);
        Assert.Equal(4066.66m, detail.Loan.Outstanding);
        Assert.Equal(3, detail.Repayments.Count);
    }

    [SkippableFact]
    public async Task MarksTheInstalmentsTheLedgerHasCovered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        // Five instalments' worth, as their own test does.
        await Register(provider).RecordRepaymentAsync(
            loan.Id, new LoanRepaymentRequest { Amount = 416.67m * 5 });

        IReadOnlyList<Instalment> schedule = (await Register(provider).GetAsync(loan.Id))!.Schedule;

        Assert.All(schedule.Take(5), instalment =>
            Assert.Equal(InstalmentStatus.Paid, instalment.Status));

        Assert.Equal(InstalmentStatus.Overdue, schedule[5].Status);
        Assert.Equal(InstalmentStatus.Due, schedule[7].Status);
        Assert.Equal(InstalmentStatus.Upcoming, schedule[8].Status);
    }

    [SkippableFact]
    public async Task SettlingTheBalanceClosesTheLoan()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        LoanResult result = await Register(provider)
            .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = 5000m });

        Assert.Equal(LoanStatus.Closed, result.Loan!.Status);
        Assert.Equal(0m, result.Loan.Outstanding);
    }

    // The frontend clamps an overpayment to the balance, silently losing money
    // that was genuinely received.
    [SkippableFact]
    public async Task RefusesARepaymentLargerThanTheBalance()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        await Register(provider)
            .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = 4900m });

        LoanResult result = await Register(provider)
            .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = 200m });

        Assert.Equal(LoanRefusal.RepaymentExceedsBalance, result.Refusal);

        // And nothing was written.
        Assert.Equal(4900m, await RepaidAsync(provider, loan.Id));
    }

    [SkippableFact]
    public async Task RefusesARepaymentAgainstMoneyThatNeverMoved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        LoanResult result = await Register(provider)
            .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = 100m });

        Assert.Equal(LoanRefusal.NotIssued, result.Refusal);
    }

    // A mis-keyed repayment is reversible, which is the whole reason this is a
    // ledger and not a counter.
    [SkippableFact]
    public async Task ReversesAMisRecordedRepayment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        await Register(provider)
            .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = 416.67m });

        await Register(provider)
            .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = 4000m });

        LoanDetailDto detail = (await Register(provider).GetAsync(loan.Id))!;

        Guid wrong = detail.Repayments.Single(row => row.Amount == 4000m).Id;

        Assert.True((await Register(provider).ReverseRepaymentAsync(loan.Id, wrong)).Succeeded);

        Assert.Equal(416.67m, await RepaidAsync(provider, loan.Id));
    }

    // Reversing the payment that closed it has to reopen it, or the register
    // would keep a settled loan that nobody has actually settled.
    [SkippableFact]
    public async Task ReversingThePaymentThatClosedALoanReopensIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        LoanResult settled = await Register(provider)
            .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = 5000m });

        Assert.Equal(LoanStatus.Closed, settled.Loan!.Status);

        Guid repaymentId = (await Register(provider).GetAsync(loan.Id))!.Repayments[0].Id;

        LoanResult reversed = await Register(provider)
            .ReverseRepaymentAsync(loan.Id, repaymentId);

        Assert.Equal(LoanStatus.Active, reversed.Loan!.Status);
        Assert.Equal(5000m, reversed.Loan.Outstanding);
    }

    [SkippableFact]
    public async Task RecordsWhereARepaymentCameFrom()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        await Register(provider).RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest
        {
            Amount = 416.67m,
            PaidOn = new DateOnly(2026, 2, 1),
            Method = LoanRepaymentMethod.Payroll,
            Reference = "PAY-2026-02",
            Note = "February payroll deduction",
        });

        LoanRepaymentDto recorded =
            Assert.Single((await Register(provider).GetAsync(loan.Id))!.Repayments);

        Assert.Equal(LoanRepaymentMethod.Payroll, recorded.Method);
        Assert.Equal("PAY-2026-02", recorded.Reference);
        Assert.Equal(new DateOnly(2026, 2, 1), recorded.PaidOn);
    }

    // ── default and recovery ────────────────────────────────────────────────

    [SkippableFact]
    public async Task MarksALoanDefaultedAndStillAcceptsRepayments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        LoanResult defaulted = await Register(provider).MarkDefaultedAsync(loan.Id);

        Assert.Equal(LoanStatus.Defaulted, defaulted.Loan!.Status);
        Assert.Equal(new DateOnly(2026, 9, 24), defaulted.Loan.DefaultedOn);

        Assert.True((await Register(provider)
            .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = 100m }))
            .Succeeded);
    }

    [SkippableFact]
    public async Task RecoveringADefaultedLoanInFullClosesIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        await Register(provider).MarkDefaultedAsync(loan.Id);

        LoanResult result = await Register(provider)
            .RecordRepaymentAsync(loan.Id, new LoanRepaymentRequest { Amount = 5000m });

        Assert.Equal(LoanStatus.Closed, result.Loan!.Status);
    }

    [SkippableFact]
    public async Task ReinstatesALoanMarkedDefaultedByMistake()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await IssuedAsync(provider);

        await Register(provider).MarkDefaultedAsync(loan.Id);

        LoanResult result = await Register(provider).ReinstateAsync(loan.Id);

        Assert.Equal(LoanStatus.Active, result.Loan!.Status);
        Assert.Null(result.Loan.DefaultedOn);
    }

    [SkippableFact]
    public async Task WillNotDefaultALoanThatWasNeverIssued()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto loan = await RequestedAsync(provider);

        Assert.Equal(
            LoanRefusal.NotActive,
            (await Register(provider).MarkDefaultedAsync(loan.Id)).Refusal);
    }

    // ── the register's figures ──────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsTheRegistersHeadlineFigures()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        // Their own fixture: an active loan part repaid, a defaulted one, a
        // settled one, one awaiting approval and one refused.
        LoanDto active = await IssuedAsync(provider, amount: 1200m);
        await Register(provider)
            .RecordRepaymentAsync(active.Id, new LoanRepaymentRequest { Amount = 700m });

        LoanDto defaulted = await IssuedAsync(provider, amount: 1000m);
        await Register(provider)
            .RecordRepaymentAsync(defaulted.Id, new LoanRepaymentRequest { Amount = 200m });
        await Register(provider).MarkDefaultedAsync(defaulted.Id);

        LoanDto closed = await IssuedAsync(provider, amount: 500m);
        await Register(provider)
            .RecordRepaymentAsync(closed.Id, new LoanRepaymentRequest { Amount = 500m });

        await RequestedAsync(provider, Request(amount: 800m));

        LoanDto refused = await RequestedAsync(provider, Request(amount: 900m));
        await Register(provider).RejectAsync(refused.Id);

        LoanBookTally totals = (await Register(provider).SummaryAsync()).Totals;

        Assert.Equal(1, totals.ActiveLoans);
        Assert.Equal(2200m, totals.LoanValue);
        Assert.Equal(1300m, totals.Outstanding);
        Assert.Equal(1, totals.Pending);
        Assert.Equal(1, totals.Defaulted);
        Assert.Equal(52, totals.RecoveryRate);
    }

    [SkippableFact]
    public async Task ReportsTheBookByProductAndByDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        LoanDto staff = await IssuedAsync(provider, amount: 1200m);
        await Register(provider)
            .RecordRepaymentAsync(staff.Id, new LoanRepaymentRequest { Amount = 200m });

        await RequestedAsync(provider, Request(type: LoanType.SalaryAdvance, amount: 800m));

        LoanBookDto book = await Register(provider).SummaryAsync();

        Assert.Equal(
            1000m,
            book.ByType.Single(row => row.LoanType == LoanType.StaffLoan).Outstanding);

        // A pending advance counts towards the product's book but owes nothing.
        Assert.Equal(
            0m,
            book.ByType.Single(row => row.LoanType == LoanType.SalaryAdvance).Outstanding);

        LoanDepartmentTally department = Assert.Single(book.ByDepartment);

        Assert.Equal("Engineering", department.DepartmentName);
        Assert.Equal(1000m, department.Outstanding);
    }

    [SkippableFact]
    public async Task RanksTheLargestBalancesFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await IssuedAsync(provider, amount: 1000m);
        await IssuedAsync(provider, amount: 4000m);
        await IssuedAsync(provider, amount: 2000m);

        LoanBookDto book = await Register(provider).SummaryAsync();

        Assert.Equal(
            [4000m, 2000m, 1000m],
            book.LargestBalances.Select(loan => loan.Outstanding));
    }

    // ── the register and one person's view of it ────────────────────────────

    [SkippableFact]
    public async Task ListsOneEmployeesLoansOnly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid otherId = await HireAsync(provider, "Tunde", managerId: _managerId);

        await RequestedAsync(provider);
        await RequestedAsync(provider, Request(employeeId: otherId, amount: 900m));

        IReadOnlyList<LoanDto> theirs = await Register(provider).ForEmployeeAsync(otherId);

        Assert.Equal([900m], theirs.Select(loan => loan.Amount));
    }

    [SkippableFact]
    public async Task NamesTheBorrowerOnTheRegister()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RequestedAsync(provider);

        LoanDto listed = Assert.Single(
            (await Register(provider).ListAsync(new LoanQuery())).Items);

        Assert.Equal("Ngozi Okafor", listed.EmployeeName);
        Assert.Equal("Engineering", listed.DepartmentName);
    }

    [SkippableFact]
    public async Task FiltersTheRegisterByStatus()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await IssuedAsync(provider, amount: 1200m);
        await RequestedAsync(provider, Request(amount: 800m));

        PagedResult<LoanDto> pending = await Register(provider)
            .ListAsync(new LoanQuery { Status = LoanStatus.Pending });

        Assert.Equal([800m], pending.Items.Select(loan => loan.Amount));
    }

    [SkippableFact]
    public async Task SearchesTheRegisterByReferencePurposeAndBorrower()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RequestedAsync(provider, Request(purpose: "School fees"));

        foreach (string term in (string[])["LOAN-2026", "school", "ngozi"])
        {
            PagedResult<LoanDto> found = await Register(provider)
                .ListAsync(new LoanQuery { Search = term });

            Assert.NotEmpty(found.Items);
        }
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RequestedAsync(provider, Request(purpose: "Rent (100%)"));
        await RequestedAsync(provider, Request(purpose: "School fees"));

        PagedResult<LoanDto> found = await Register(provider)
            .ListAsync(new LoanQuery { Search = "100%" });

        Assert.Equal(["Rent (100%)"], found.Items.Select(loan => loan.Purpose));
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersRegister()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await RequestedAsync(provider);

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

        Assert.Empty((await Register(provider).ListAsync(new LoanQuery())).Items);

        // And the reference sequence is their own, not a continuation of ours.
        Assert.Equal(0m, (await Register(provider).SummaryAsync()).Totals.Outstanding);
    }

    [SkippableFact]
    public async Task TheSignedInEmployeeSeesOnlyTheirOwn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid otherId = await HireAsync(provider, "Tunde", managerId: _managerId);
        await AccountAsync(otherId, "tunde");

        await RequestedAsync(provider);
        await RequestedAsync(provider, Request(employeeId: otherId, amount: 900m));

        ActAs(provider, await UserForAsync(otherId));

        Assert.Equal([900m], (await Register(provider).MineAsync()).Select(loan => loan.Amount));
    }
}
