using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Application.Countries;
using Motee.Application.Expenses;
using Motee.Application.Tenancy;
using Motee.Domain.Approvals;
using Motee.Domain.Expenses;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Expenses;

internal sealed class ExpenseClaimService(
    MoteeDbContext dbContext,
    IApprovalService approvals,
    ICountryProfileProvider countries,
    ICurrentEmployee currentEmployee,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IExpenseClaimService
{
    private const string SubjectType = "expense_claim";

    public async Task<PagedResult<ExpenseClaimDto>> MineAsync(
        ExpenseQuery query,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return Empty(query);
        }

        // Their own, whatever the query said about whose — a client passing somebody
        // else's id must not read a colleague's claims through the self-service door.
        return await PageAsync(query with { EmployeeId = employeeId }, cancellationToken);
    }

    public Task<PagedResult<ExpenseClaimDto>> ListAsync(
        ExpenseQuery query,
        CancellationToken cancellationToken = default) =>
        PageAsync(query, cancellationToken);

    public async Task<ExpenseClaimDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ExpenseClaim? claim = await dbContext.ExpenseClaims
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return claim is null ? null : (await DescribeAsync([claim], cancellationToken))[0];
    }

    public async Task<ExpenseResult> CreateAsync(
        ExpenseClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return ExpenseResult.Failed(ExpenseOutcome.EmployeeNotFound);
        }

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        if (Validate(request, today) is ExpenseOutcome problem)
        {
            return ExpenseResult.Failed(problem);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ExpenseClaim claim = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            Title = request.Title.Trim(),
            Category = request.Category,
            Amount = request.Amount,
            Currency = await CurrencyAsync(request.Currency, cancellationToken),
            Merchant = request.Merchant.Trim(),
            IncurredOn = request.IncurredOn,
            Notes = request.Notes?.Trim(),
            ReceiptFileIds = [.. request.ReceiptFileIds ?? []],

            // Draft, always. Filing and submitting are separate acts, and creating
            // straight into the chain would mean no way to save a half-written claim.
            Status = ExpenseStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.ExpenseClaims.Add(claim);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ExpenseResult.Ok((await DescribeAsync([claim], cancellationToken))[0]);
    }

    public async Task<ExpenseResult> UpdateAsync(
        Guid id,
        ExpenseClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await MineOrNullAsync(id, cancellationToken) is not ExpenseClaim claim)
        {
            return ExpenseResult.Failed(ExpenseOutcome.NotFound);
        }

        // Only a draft is editable. Once it is with somebody, changing the amount
        // under them means they approve one figure and the company pays another.
        if (claim.Status != ExpenseStatus.Draft)
        {
            return ExpenseResult.Failed(
                ExpenseOutcome.NotAllowed,
                "This claim is no longer a draft. Withdraw it first if it needs changing.");
        }

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        if (Validate(request, today) is ExpenseOutcome problem)
        {
            return ExpenseResult.Failed(problem);
        }

        claim.Title = request.Title.Trim();
        claim.Category = request.Category;
        claim.Amount = request.Amount;
        claim.Currency = await CurrencyAsync(request.Currency, cancellationToken);
        claim.Merchant = request.Merchant.Trim();
        claim.IncurredOn = request.IncurredOn;
        claim.Notes = request.Notes?.Trim();
        claim.ReceiptFileIds = [.. request.ReceiptFileIds ?? []];
        claim.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ExpenseResult.Ok((await DescribeAsync([claim], cancellationToken))[0]);
    }

    public async Task<ExpenseResult> SubmitAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await MineOrNullAsync(id, cancellationToken) is not ExpenseClaim claim)
        {
            return ExpenseResult.Failed(ExpenseOutcome.NotFound);
        }

        if (!ExpenseLifecycle.Can(claim.Status, ExpenseAction.Submit))
        {
            return ExpenseResult.Failed(
                ExpenseOutcome.NotAllowed,
                ExpenseLifecycle.Refusal(claim.Status, ExpenseAction.Submit));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Issued on first submission and kept for ever after, including through a
        // withdrawal and a resubmission. A claim that changed its reference halfway
        // through would be two claims in every email about it.
        claim.Reference ??= await NextReferenceAsync(now.Year, cancellationToken);

        ApprovalResult review = await approvals.StartAsync(
            new StartApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.ExpenseClaim,
                SubjectType = SubjectType,
                SubjectId = claim.Id,
                SubjectEmployeeId = claim.EmployeeId,
            },
            cancellationToken);

        // Refused, not stored-and-ignored. Onboarding lets a submission stand with no
        // reviewer because the joiner cannot fix their company's configuration and
        // should not be blocked by it. A claim is the opposite: nobody is waiting on
        // the claim, the person is waiting on their money, and "submitted" with no
        // reviewer means it sits unpaid with nothing to chase.
        if (!review.Succeeded)
        {
            return ExpenseResult.Failed(ExpenseOutcome.NoApprovalChain, review.Reason);
        }

        claim.ApprovalInstanceId = review.Approval!.Id;
        claim.Status = ExpenseStatus.Submitted;
        claim.SubmittedAt = now;
        claim.UpdatedAt = now;

        // Cleared on resubmission, or a corrected claim still shows why it was sent
        // back and reads as though it had been returned twice.
        claim.Returned = false;
        claim.ReturnedReason = null;

        await dbContext.SaveChangesAsync(cancellationToken);

        return ExpenseResult.Ok((await DescribeAsync([claim], cancellationToken))[0]);
    }

    public async Task<ExpenseResult> WithdrawAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await MineOrNullAsync(id, cancellationToken) is not ExpenseClaim claim)
        {
            return ExpenseResult.Failed(ExpenseOutcome.NotFound);
        }

        if (!ExpenseLifecycle.Can(claim.Status, ExpenseAction.Withdraw))
        {
            return ExpenseResult.Failed(
                ExpenseOutcome.NotAllowed,
                ExpenseLifecycle.Refusal(claim.Status, ExpenseAction.Withdraw));
        }

        await CancelReviewAsync(claim, "Withdrawn by the person who filed it.", cancellationToken);

        claim.Status = ExpenseStatus.Draft;
        claim.SubmittedAt = null;
        claim.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ExpenseResult.Ok((await DescribeAsync([claim], cancellationToken))[0]);
    }

    public async Task<ExpenseResult> ReturnAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ExpenseResult.Failed(ExpenseOutcome.ReasonRequired);
        }

        ExpenseClaim? claim = await dbContext.ExpenseClaims
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (claim is null)
        {
            return ExpenseResult.Failed(ExpenseOutcome.NotFound);
        }

        if (!ExpenseLifecycle.Can(claim.Status, ExpenseAction.Return))
        {
            return ExpenseResult.Failed(
                ExpenseOutcome.NotAllowed,
                ExpenseLifecycle.Refusal(claim.Status, ExpenseAction.Return));
        }

        await CancelReviewAsync(claim, reason.Trim(), cancellationToken);

        // Back to a draft, flagged. The flag is what lets the screen say "sent back"
        // on something that is otherwise an ordinary editable claim.
        claim.Status = ExpenseStatus.Draft;
        claim.Returned = true;
        claim.ReturnedReason = reason.Trim();
        claim.SubmittedAt = null;
        claim.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ExpenseResult.Ok((await DescribeAsync([claim], cancellationToken))[0]);
    }

    public async Task<ExpenseResult> ReimburseAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ExpenseClaim? claim = await dbContext.ExpenseClaims
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (claim is null)
        {
            return ExpenseResult.Failed(ExpenseOutcome.NotFound);
        }

        if (!ExpenseLifecycle.Can(claim.Status, ExpenseAction.Reimburse))
        {
            return ExpenseResult.Failed(
                ExpenseOutcome.NotAllowed,
                ExpenseLifecycle.Refusal(claim.Status, ExpenseAction.Reimburse));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        claim.Status = ExpenseStatus.Reimbursed;
        claim.ReimbursedAt = now;
        claim.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return ExpenseResult.Ok((await DescribeAsync([claim], cancellationToken))[0]);
    }

    public async Task<ExpenseOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await MineOrNullAsync(id, cancellationToken) is not ExpenseClaim claim)
        {
            return ExpenseOutcome.NotFound;
        }

        // Drafts only. A submitted claim is part of somebody else's queue and a paid
        // one is part of the company's books; neither is the filer's to erase.
        if (claim.Status != ExpenseStatus.Draft)
        {
            return ExpenseOutcome.NotAllowed;
        }

        dbContext.ExpenseClaims.Remove(claim);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ExpenseOutcome.Succeeded;
    }

    // Whatever is running is no longer the question being asked. Cancelled rather
    // than left open, so an approver does not decide a claim that has been pulled
    // out from under them.
    private async Task CancelReviewAsync(
        ExpenseClaim claim,
        string reason,
        CancellationToken cancellationToken)
    {
        if (claim.ApprovalInstanceId is not Guid instanceId)
        {
            return;
        }

        await approvals.CancelAsync(instanceId, reason, cancellationToken);

        claim.ApprovalInstanceId = null;
    }

    // Scoped to the caller, so a valid id is not permission to act on somebody
    // else's claim through the self-service routes.
    private async Task<ExpenseClaim?> MineOrNullAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return null;
        }

        return await dbContext.ExpenseClaims
            .FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.EmployeeId == employeeId,
                cancellationToken);
    }

    private static ExpenseOutcome? Validate(ExpenseClaimRequest request, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return ExpenseOutcome.TitleRequired;
        }

        if (string.IsNullOrWhiteSpace(request.Merchant))
        {
            return ExpenseOutcome.MerchantRequired;
        }

        if (request.Amount <= 0m)
        {
            return ExpenseOutcome.AmountNotPositive;
        }

        // Shape only, not the ISO list. That list changes, and a company trading
        // somewhere unusual should not be told their currency does not exist — but a
        // three-letter check still catches "Pounds" and "£".
        if (request.Currency is { Length: > 0 } currency
            && (currency.Length != 3 || !currency.All(char.IsAsciiLetter)))
        {
            return ExpenseOutcome.CurrencyInvalid;
        }

        return request.IncurredOn > today ? ExpenseOutcome.IncurredInTheFuture : null;
    }

    // The company's own, from its country, unless the claim says otherwise. Somebody
    // filing in a foreign currency is the exception worth typing.
    private async Task<string> CurrencyAsync(string? requested, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim().ToUpperInvariant();
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return countries.Get(Domain.Common.CountryCode.Nigeria).CurrencyCode;
        }

        Domain.Common.CountryCode country = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.CountryCode)
            .FirstOrDefaultAsync(cancellationToken);

        return countries.Get(country).CurrencyCode;
    }

    // Per year, continuing from the highest already issued. Read rather than counted,
    // so withdrawing and resubmitting never hands out a reference twice — the claim
    // keeps the one it has and nothing else reuses it.
    private async Task<string> NextReferenceAsync(int year, CancellationToken cancellationToken)
    {
        string prefix = $"{ExpenseReferences.Prefix}-{year}-";

        List<string> issued = await dbContext.ExpenseClaims
            .AsNoTracking()
            .Where(claim => claim.Reference != null && claim.Reference.StartsWith(prefix))
            .Select(claim => claim.Reference!)
            .ToListAsync(cancellationToken);

        int highest = issued.Aggregate(
            0,
            (max, reference) =>
                ExpenseReferences.TrySequenceOf(reference, year, out int sequence) && sequence > max
                    ? sequence
                    : max);

        return ExpenseReferences.Format(year, highest + 1);
    }

    private static PagedResult<ExpenseClaimDto> Empty(ExpenseQuery query) => new()
    {
        Items = [],
        Page = query.Page,
        PageSize = query.PageSize,
        TotalItems = 0,
    };

    private async Task<PagedResult<ExpenseClaimDto>> PageAsync(
        ExpenseQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<ExpenseClaim> source = dbContext.ExpenseClaims.AsNoTracking();

        if (query.EmployeeId is Guid employeeId)
        {
            source = source.Where(claim => claim.EmployeeId == employeeId);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            source = source.Where(claim => dbContext.Employees
                .Any(employee => employee.Id == claim.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        if (query.Status is ExpenseStatus status)
        {
            source = source.Where(claim => claim.Status == status);
        }

        if (query.Category is ExpenseCategory category)
        {
            source = source.Where(claim => claim.Category == category);
        }

        if (query.From is DateOnly from)
        {
            source = source.Where(claim => claim.IncurredOn >= from);
        }

        if (query.To is DateOnly to)
        {
            source = source.Where(claim => claim.IncurredOn <= to);
        }

        int total = await source.CountAsync(cancellationToken);

        List<ExpenseClaim> page = await source
            .OrderByDescending(claim => claim.IncurredOn)
            .ThenByDescending(claim => claim.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ExpenseClaimDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    private async Task<IReadOnlyList<ExpenseClaimDto>> DescribeAsync(
        IReadOnlyList<ExpenseClaim> claims,
        CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        List<Guid> employeeIds = [.. claims.Select(claim => claim.EmployeeId).Distinct()];

        Dictionary<Guid, (string Name, string? Department)> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                Department = dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(row => row.Id, row => (row.Name, row.Department), cancellationToken);

        List<ExpenseClaimDto> described = [];

        foreach (ExpenseClaim claim in claims)
        {
            ApprovalDto? review = claim.ApprovalInstanceId is Guid instanceId
                ? await approvals.GetAsync(instanceId, cancellationToken)
                : null;

            described.Add(new ExpenseClaimDto
            {
                Id = claim.Id,
                EmployeeId = claim.EmployeeId,
                EmployeeName = people.TryGetValue(claim.EmployeeId, out (string Name, string? Department) person)
                    ? person.Name
                    : "Unknown",
                DepartmentName = people.GetValueOrDefault(claim.EmployeeId).Department,
                Reference = claim.Reference,
                Title = claim.Title,
                Category = claim.Category,
                Amount = claim.Amount,
                Currency = claim.Currency,
                Merchant = claim.Merchant,
                IncurredOn = claim.IncurredOn,
                Notes = claim.Notes,
                Status = claim.Status,
                Returned = claim.Returned,
                ReturnedReason = claim.ReturnedReason,
                ReceiptFileIds = claim.ReceiptFileIds,
                Review = review,
                SubmittedAt = claim.SubmittedAt,
                ReimbursedAt = claim.ReimbursedAt,
                CreatedAt = claim.CreatedAt,
            });
        }

        return described;
    }
}
