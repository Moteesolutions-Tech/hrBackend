using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Application.Loans;
using Motee.Application.Tenancy;
using Motee.Domain.Approvals;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Loans;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Loans;

// The staff loan register.
//
// Two derivations carry this module. The status comes from the approval chain,
// the default date and the ledger — in that order — so a loan cannot read
// "pending" after Finance approved it or "defaulted" after the debt was
// settled. And the amount repaid is the sum of the repayment ledger rather
// than a column, because that figure decides what the company tells somebody
// they still owe, and a counter that drifts there is the worst bug this module
// could have.
internal sealed class LoanService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    IApprovalService approvals,
    TimeProvider timeProvider) : ILoanService
{
    private const string SubjectType = "employee_loan";

    private TenantFacts? _tenant;

    public async Task<PagedResult<LoanDto>> ListAsync(
        LoanQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<EmployeeLoan> loans = dbContext.EmployeeLoans.AsNoTracking();

        if (query.LoanType is LoanType type)
        {
            loans = loans.Where(loan => loan.LoanType == type);
        }

        if (query.EmployeeId is Guid employeeId)
        {
            loans = loans.Where(loan => loan.EmployeeId == employeeId);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            List<Guid> inDepartment = await dbContext.Employees
                .AsNoTracking()
                .Where(employee => employee.DepartmentId == departmentId)
                .Select(employee => employee.Id)
                .ToListAsync(cancellationToken);

            loans = loans.Where(loan => inDepartment.Contains(loan.EmployeeId));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            // The borrower's name is on the employee row, so the matching ids
            // are resolved first rather than joining on every filtered read.
            List<Guid> named = await dbContext.Employees
                .AsNoTracking()
                .Where(employee => EF.Functions.ILike(
                    employee.FirstName + " " + employee.LastName, pattern, "\\"))
                .Select(employee => employee.Id)
                .ToListAsync(cancellationToken);

            loans = loans.Where(loan =>
                EF.Functions.ILike(loan.Reference, pattern, "\\")
                || EF.Functions.ILike(loan.Purpose, pattern, "\\")
                || named.Contains(loan.EmployeeId));
        }

        // The status is derived from the chain and the ledger, so it cannot be
        // filtered or paged in SQL. Everything above narrows first, and only a
        // status filter falls back to reading the narrowed set.
        List<EmployeeLoan> rows = await loans
            .OrderByDescending(loan => loan.RequestedOn)
            .ThenByDescending(loan => loan.CreatedAt)
            .ToListAsync(cancellationToken);

        List<LoanDto> described = await DescribeAsync(rows, cancellationToken);

        if (query.Status is LoanStatus status)
        {
            described = [.. described.Where(loan => loan.Status == status)];
        }

        return new PagedResult<LoanDto>
        {
            Items = [.. described.Skip(query.Skip).Take(query.PageSize)],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = described.Count,
        };
    }

    public async Task<LoanDetailDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EmployeeLoan? loan = await dbContext.EmployeeLoans
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (loan is null)
        {
            return null;
        }

        LoanDto described = (await DescribeAsync([loan], cancellationToken))[0];

        List<LoanRepayment> ledger = await LedgerAsync(id, cancellationToken);

        return new LoanDetailDto
        {
            Loan = described,
            Schedule = EmployeeLoans.Schedule(
                loan,
                described.Status,
                described.AmountRepaid,
                await TodayAsync(cancellationToken)),
            Repayments =
            [
                .. ledger.Select(row => new LoanRepaymentDto
                {
                    Id = row.Id,
                    Amount = row.Amount,
                    PaidOn = row.PaidOn,
                    Method = row.Method,
                    Reference = row.Reference,
                    Note = row.Note,
                    RecordedAt = row.CreatedAt,
                }),
            ],
        };
    }

    public async Task<LoanBookDto> SummaryAsync(CancellationToken cancellationToken = default)
    {
        List<EmployeeLoan> rows = await dbContext.EmployeeLoans
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<LoanDto> described = await DescribeAsync(rows, cancellationToken);

        DateOnly today = await TodayAsync(cancellationToken);

        List<LoanBookEntry> entries =
        [
            .. described.Select(loan => new LoanBookEntry
            {
                Status = loan.Status,
                LoanType = loan.LoanType,
                Amount = loan.Amount,
                Repaid = loan.AmountRepaid,
                IssuedOn = loan.IssuedOn,
                RepaymentMonths = loan.RepaymentMonths,
            }),
        ];

        return new LoanBookDto
        {
            Totals = LoanBook.Of(entries, today),
            ByType = LoanBook.ByType(entries),

            // Only departments with something owed. A company with one loan
            // should not get a chart of zeros for every other team.
            ByDepartment =
            [
                .. described
                    .Where(loan => loan.Outstanding > 0m)
                    .GroupBy(loan => new { loan.DepartmentName })
                    .Select(group => new LoanDepartmentTally
                    {
                        DepartmentName = group.Key.DepartmentName ?? "Unassigned",
                        Outstanding = EmployeeLoans.Round(
                            group.Sum(loan => loan.Outstanding)),
                        Loans = group.Count(),
                    })
                    .OrderByDescending(row => row.Outstanding)
                    .ThenBy(row => row.DepartmentName, StringComparer.Ordinal),
            ],
            LargestBalances =
            [
                .. described
                    .Where(loan => loan.Outstanding > 0m)
                    .OrderByDescending(loan => loan.Outstanding)
                    .ThenBy(loan => loan.Reference, StringComparer.Ordinal)
                    .Take(5),
            ],
        };
    }

    public async Task<LoanResult> RequestAsync(
        LoanRequest request,
        CancellationToken cancellationToken = default)
    {
        // Omitted means the signed-in employee is applying for themselves.
        Guid? subjectId = request.EmployeeId ?? await currentEmployee.IdAsync(cancellationToken);

        if (subjectId is not Guid borrowerId)
        {
            return LoanResult.Failed(LoanOutcome.EmployeeNotFound);
        }

        bool employed = await dbContext.Employees.AnyAsync(
            employee => employee.Id == borrowerId
                && employee.Status != EmployeeStatus.Deleted,
            cancellationToken);

        if (!employed)
        {
            return LoanResult.Failed(LoanOutcome.EmployeeNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        TenantFacts tenant = await TenantAsync(cancellationToken);
        DateOnly today = TenantClock.DayOf(now, tenant.TimeZoneId);

        EmployeeLoan loan = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Reference = await NextReferenceAsync(today.Year, cancellationToken),
            EmployeeId = borrowerId,
            LoanType = request.LoanType,
            Amount = EmployeeLoans.Round(request.Amount),
            Currency = tenant.Currency,
            Purpose = request.Purpose?.Trim() ?? string.Empty,
            RepaymentMonths = request.RepaymentMonths,
            InterestRatePercent = request.InterestRatePercent,
            RequestedOn = today,
            CreatedAt = now,
            UpdatedAt = now,
        };

        LoanRefusal refusal = EmployeeLoans.Refusal(loan, tenant.Country);

        if (refusal != LoanRefusal.None)
        {
            return LoanResult.Refused(refusal);
        }

        ApprovalResult started = await approvals.StartAsync(
            new StartApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.LoanRequest,
                SubjectType = SubjectType,
                SubjectId = loan.Id,
                SubjectEmployeeId = borrowerId,
                FileIds = request.FileIds,
            },
            cancellationToken);

        // A company with no loan chain configured still has to be able to keep
        // a register: the loan is recorded and decided by hand instead. What
        // must not happen is the request vanishing because a template is
        // missing.
        loan.ApprovalInstanceId = started.Succeeded ? started.Approval?.Id : null;

        dbContext.EmployeeLoans.Add(loan);

        await dbContext.SaveChangesAsync(cancellationToken);

        return LoanResult.Ok((await DescribeAsync([loan], cancellationToken))[0]);
    }

    public async Task<LoanResult> ApproveAsync(
        Guid id,
        DateOnly? issuedOn = null,
        CancellationToken cancellationToken = default)
    {
        EmployeeLoan? loan = await FindAsync(id, cancellationToken);

        if (loan is null)
        {
            return LoanResult.Failed(LoanOutcome.NotFound);
        }

        loan.IssuedOn = issuedOn ?? await TodayAsync(cancellationToken);
        loan.RejectedOn = null;
        loan.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return LoanResult.Ok((await DescribeAsync([loan], cancellationToken))[0]);
    }

    public async Task<LoanResult> RejectAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EmployeeLoan? loan = await FindAsync(id, cancellationToken);

        if (loan is null)
        {
            return LoanResult.Failed(LoanOutcome.NotFound);
        }

        loan.RejectedOn = await TodayAsync(cancellationToken);
        loan.IssuedOn = null;
        loan.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return LoanResult.Ok((await DescribeAsync([loan], cancellationToken))[0]);
    }

    public async Task<LoanResult> MarkDefaultedAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EmployeeLoan? loan = await FindAsync(id, cancellationToken);

        if (loan is null)
        {
            return LoanResult.Failed(LoanOutcome.NotFound);
        }

        LoanDto described = (await DescribeAsync([loan], cancellationToken))[0];

        // Writing off something that is not running: nothing was lent, or it
        // is already settled.
        if (described.Status is not (LoanStatus.Active or LoanStatus.Defaulted))
        {
            return LoanResult.Refused(LoanRefusal.NotActive);
        }

        loan.DefaultedOn = await TodayAsync(cancellationToken);
        loan.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return LoanResult.Ok((await DescribeAsync([loan], cancellationToken))[0]);
    }

    public async Task<LoanResult> ReinstateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EmployeeLoan? loan = await FindAsync(id, cancellationToken);

        if (loan is null)
        {
            return LoanResult.Failed(LoanOutcome.NotFound);
        }

        loan.DefaultedOn = null;
        loan.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return LoanResult.Ok((await DescribeAsync([loan], cancellationToken))[0]);
    }

    public async Task<LoanResult> RecordRepaymentAsync(
        Guid id,
        LoanRepaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        EmployeeLoan? loan = await FindAsync(id, cancellationToken);

        if (loan is null)
        {
            return LoanResult.Failed(LoanOutcome.NotFound);
        }

        LoanDto described = (await DescribeAsync([loan], cancellationToken))[0];

        decimal amount = EmployeeLoans.Round(request.Amount);

        LoanRefusal refusal = EmployeeLoans.CanRecordRepayment(
            described.Status, amount, described.Outstanding);

        if (refusal != LoanRefusal.None)
        {
            return LoanResult.Refused(refusal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        dbContext.LoanRepayments.Add(new LoanRepayment
        {
            Id = Guid.NewGuid(),
            TenantId = loan.TenantId,
            LoanId = loan.Id,
            Amount = amount,
            PaidOn = request.PaidOn ?? await TodayAsync(cancellationToken),
            Method = request.Method,
            Reference = Blank(request.Reference),
            Note = Blank(request.Note),
            CreatedAt = now,
        });

        // Touched so the register's ordering and any caching move with the
        // money, even though nothing on the loan row itself changed.
        loan.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return LoanResult.Ok((await DescribeAsync([loan], cancellationToken))[0]);
    }

    public async Task<LoanResult> ReverseRepaymentAsync(
        Guid id,
        Guid repaymentId,
        CancellationToken cancellationToken = default)
    {
        EmployeeLoan? loan = await FindAsync(id, cancellationToken);

        if (loan is null)
        {
            return LoanResult.Failed(LoanOutcome.NotFound);
        }

        LoanRepayment? repayment = await dbContext.LoanRepayments
            .FirstOrDefaultAsync(
                row => row.Id == repaymentId && row.LoanId == id, cancellationToken);

        if (repayment is null)
        {
            return LoanResult.Failed(LoanOutcome.NotFound);
        }

        dbContext.LoanRepayments.Remove(repayment);

        loan.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        // The status follows the ledger, so reversing the payment that closed
        // a loan reopens it with no second decision to make.
        return LoanResult.Ok((await DescribeAsync([loan], cancellationToken))[0]);
    }

    public async Task<IReadOnlyList<LoanDto>> ForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        List<EmployeeLoan> rows = await dbContext.EmployeeLoans
            .AsNoTracking()
            .Where(loan => loan.EmployeeId == employeeId)
            .OrderByDescending(loan => loan.RequestedOn)
            .ThenByDescending(loan => loan.CreatedAt)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(rows, cancellationToken);
    }

    public async Task<IReadOnlyList<LoanDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        // Resolved from the session rather than taken as a parameter, so no
        // caller can read somebody else's by changing an id.
        Guid? mine = await currentEmployee.IdAsync(cancellationToken);

        return mine is Guid employeeId
            ? await ForEmployeeAsync(employeeId, cancellationToken)
            : [];
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<EmployeeLoan?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.EmployeeLoans.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    private Task<List<LoanRepayment>> LedgerAsync(
        Guid loanId,
        CancellationToken cancellationToken) =>
        dbContext.LoanRepayments
            .AsNoTracking()
            .Where(row => row.LoanId == loanId)
            .OrderBy(row => row.PaidOn)
            .ThenBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

    private async Task<List<LoanDto>> DescribeAsync(
        IReadOnlyList<EmployeeLoan> loans,
        CancellationToken cancellationToken)
    {
        if (loans.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. loans.Select(loan => loan.Id)];

        // One grouped sum for the whole page rather than one query per loan.
        Dictionary<Guid, decimal> repaid = await dbContext.LoanRepayments
            .AsNoTracking()
            .Where(row => ids.Contains(row.LoanId))
            .GroupBy(row => row.LoanId)
            .Select(group => new { LoanId = group.Key, Total = group.Sum(row => row.Amount) })
            .ToDictionaryAsync(row => row.LoanId, row => row.Total, cancellationToken);

        List<Guid> employeeIds = [.. loans.Select(loan => loan.EmployeeId).Distinct()];

        Dictionary<Guid, Borrower> borrowers = await dbContext.Employees
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
                row => new Borrower(row.Name, row.DepartmentId),
                cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id,
                department => department.Name,
                cancellationToken);

        // The chains behind these loans, read once. A loan that has one is
        // told what it says; a loan without one was decided by hand.
        List<Guid> chainIds =
        [
            .. loans
                .Where(loan => loan.ApprovalInstanceId is not null)
                .Select(loan => loan.ApprovalInstanceId!.Value)
                .Distinct(),
        ];

        Dictionary<Guid, ApprovalDto> chains = [];

        foreach (Guid chainId in chainIds)
        {
            ApprovalDto? chain = await approvals.GetAsync(chainId, cancellationToken);

            if (chain is not null)
            {
                chains[chainId] = chain;
            }
        }

        DateOnly today = await TodayAsync(cancellationToken);
        TenantFacts tenant = await TenantAsync(cancellationToken);

        return [.. loans.Select(loan => Describe(loan))];

        LoanDto Describe(EmployeeLoan loan)
        {
            decimal paid = repaid.GetValueOrDefault(loan.Id);

            ApprovalDto? chain = loan.ApprovalInstanceId is Guid chainId
                ? chains.GetValueOrDefault(chainId)
                : null;

            // The chain's decision date is the issue date where nobody has
            // recorded one, which is what keeps the register from waiting on a
            // second write after Finance approves.
            DateOnly? approvedOn = chain?.Status == ApprovalStatus.Approved
                ? DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTime(
                        chain.DecidedAt ?? timeProvider.GetUtcNow(),
                        TenantClock.Zone(tenant.TimeZoneId)).DateTime)
                : null;

            LoanStatus status = EmployeeLoans.Derive(loan, chain?.Status, approvedOn, paid);

            Borrower? borrower = borrowers.GetValueOrDefault(loan.EmployeeId);

            // Resolved so the schedule and the next instalment are read off
            // the same issue date the status was derived from.
            EmployeeLoan resolved = loan.IssuedOn is null
                ? Copy(loan, EmployeeLoans.IssuedOn(loan, approvedOn))
                : loan;

            return new LoanDto
            {
                Id = loan.Id,
                Reference = loan.Reference,
                EmployeeId = loan.EmployeeId,
                EmployeeName = borrower?.Name,
                DepartmentName = borrower?.DepartmentId is Guid departmentId
                    ? departments.GetValueOrDefault(departmentId)
                    : null,
                LoanType = loan.LoanType,
                Amount = loan.Amount,
                Currency = loan.Currency,
                Purpose = loan.Purpose,
                RepaymentMonths = loan.RepaymentMonths,
                MonthlyRepayment = EmployeeLoans.MonthlyRepayment(
                    loan.Amount, loan.RepaymentMonths),
                AmountRepaid = EmployeeLoans.Round(paid),
                Outstanding = EmployeeLoans.Outstanding(status, loan.Amount, paid),
                InterestRatePercent = loan.InterestRatePercent,
                RequestedOn = loan.RequestedOn,
                IssuedOn = resolved.IssuedOn,
                DefaultedOn = loan.DefaultedOn,
                Status = status,
                ApprovalInstanceId = loan.ApprovalInstanceId,
                ApprovalStatus = chain?.Status,
                NextInstalment = EmployeeLoans.NextInstalment(resolved, status, paid, today),
                UpdatedAt = loan.UpdatedAt,
            };
        }
    }

    // A detached copy carrying the resolved issue date. The tracked row is
    // left alone: the chain's decision is not something this read should write
    // back, and writing it would make every list a mutation.
    private static EmployeeLoan Copy(EmployeeLoan loan, DateOnly? issuedOn) => new()
    {
        Id = loan.Id,
        TenantId = loan.TenantId,
        Reference = loan.Reference,
        EmployeeId = loan.EmployeeId,
        LoanType = loan.LoanType,
        Amount = loan.Amount,
        Currency = loan.Currency,
        Purpose = loan.Purpose,
        RepaymentMonths = loan.RepaymentMonths,
        InterestRatePercent = loan.InterestRatePercent,
        RequestedOn = loan.RequestedOn,
        IssuedOn = issuedOn,
        RejectedOn = loan.RejectedOn,
        DefaultedOn = loan.DefaultedOn,
        ApprovalInstanceId = loan.ApprovalInstanceId,
        CreatedAt = loan.CreatedAt,
        UpdatedAt = loan.UpdatedAt,
    };

    // LOAN-2026-00042. Counted off what this tenant has issued this year, read
    // inside the same save as the write that claims it — the unique index is
    // what makes two tabs safe.
    private async Task<string> NextReferenceAsync(int year, CancellationToken cancellationToken)
    {
        string prefix = $"LOAN-{year}-";

        List<string> issued = await dbContext.EmployeeLoans
            .AsNoTracking()
            .Where(loan => loan.Reference.StartsWith(prefix))
            .Select(loan => loan.Reference)
            .ToListAsync(cancellationToken);

        int highest = issued
            .Select(reference =>
                int.TryParse(reference[prefix.Length..], out int number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();

        return EmployeeLoans.Reference(year, highest + 1);
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(
            timeProvider.GetUtcNow(),
            (await TenantAsync(cancellationToken)).TimeZoneId);

    // Scoped by id, because tenants are not themselves tenant-scoped — an
    // unfiltered read returns some arbitrary company's row and would offer a
    // Nigerian loan product to a UK register.
    private async Task<TenantFacts> TenantAsync(CancellationToken cancellationToken)
    {
        if (_tenant is not null)
        {
            return _tenant;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return new TenantFacts(CountryCode.Nigeria, null, "NGN");
        }

        Tenant? tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == tenantId, cancellationToken);

        CountryCode country = tenant?.CountryCode ?? CountryCode.Nigeria;

        _tenant = new TenantFacts(
            country,
            tenant?.Settings?.TimeZoneId,
            CurrencyOf(country));

        return _tenant;
    }

    // From the country, because nothing records a company currency — the
    // frontend derives it the same way. Stamped onto the loan at the point it
    // is agreed rather than read back later, so a loan keeps the currency it
    // was agreed in whatever the company's reporting currency becomes.
    private static string CurrencyOf(CountryCode country) =>
        country == CountryCode.UnitedKingdom ? "GBP" : "NGN";

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Borrower(string Name, Guid? DepartmentId);

    private sealed record TenantFacts(
        CountryCode Country,
        string? TimeZoneId,
        string Currency);
}
