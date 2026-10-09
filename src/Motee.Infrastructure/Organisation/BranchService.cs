using Microsoft.EntityFrameworkCore;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Organisation;

internal sealed class BranchService(MoteeDbContext dbContext, TimeProvider timeProvider)
    : IBranchService
{
    public async Task<IReadOnlyList<BranchDto>> ListAsync(
        CancellationToken cancellationToken = default) =>
        [
            .. (await Counted(dbContext.Branches.OrderBy(branch => branch.Name))
                .ToListAsync(cancellationToken))
                .Select(Describe),
        ];

    public async Task<BranchDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Row? row = await Counted(dbContext.Branches.Where(branch => branch.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : Describe(row);
    }

    public async Task<BranchResult> CreateAsync(
        BranchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ProblemAsync(request, null, cancellationToken) is BranchOutcome problem)
        {
            return BranchResult.Failed(problem);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Branch branch = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Code = request.Code.Trim().ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apply(branch, request, now);

        dbContext.Branches.Add(branch);

        await dbContext.SaveChangesAsync(cancellationToken);

        return BranchResult.Ok((await GetAsync(branch.Id, cancellationToken))!);
    }

    public async Task<BranchResult> UpdateAsync(
        Guid id,
        BranchRequest request,
        CancellationToken cancellationToken = default)
    {
        Branch? branch = await dbContext.Branches
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (branch is null)
        {
            return BranchResult.Failed(BranchOutcome.NotFound);
        }

        if (await ProblemAsync(request, id, cancellationToken) is BranchOutcome problem)
        {
            return BranchResult.Failed(problem);
        }

        branch.Name = request.Name.Trim();
        branch.Code = request.Code.Trim().ToUpperInvariant();

        Apply(branch, request, timeProvider.GetUtcNow());

        await dbContext.SaveChangesAsync(cancellationToken);

        return BranchResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<BranchOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Branch? branch = await dbContext.Branches
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (branch is null)
        {
            return BranchOutcome.NotFound;
        }

        // Anybody at all, including leavers. Their record still says where they worked,
        // and deleting the site would leave it pointing at nothing — which reads as "no
        // branch" and drops them out of every site-scoped list, a fire register included.
        //
        // Deactivating is the way to close a site. The screen offers reassignment first.
        bool posted = await dbContext.Employees
            .AnyAsync(employee => employee.BranchId == id, cancellationToken);

        if (posted)
        {
            return BranchOutcome.InUse;
        }

        dbContext.Branches.Remove(branch);

        await dbContext.SaveChangesAsync(cancellationToken);

        return BranchOutcome.Succeeded;
    }

    private async Task<BranchOutcome?> ProblemAsync(
        BranchRequest request,
        Guid? excluding,
        CancellationToken cancellationToken)
    {
        string code = request.Code.Trim().ToUpperInvariant();

        bool taken = await dbContext.Branches.AnyAsync(
            other => other.Code == code && (excluding == null || other.Id != excluding),
            cancellationToken);

        if (taken)
        {
            return BranchOutcome.DuplicateCode;
        }

        if (request.ManagerEmployeeId is not Guid manager)
        {
            return null;
        }

        // The tenant filter answers "another company's employee" on its own — they are
        // simply not found — so this one check covers both a typo and a cross-tenant id.
        bool exists = await dbContext.Employees
            .AnyAsync(employee => employee.Id == manager, cancellationToken);

        return exists ? null : BranchOutcome.UnknownManager;
    }

    private static void Apply(Branch branch, BranchRequest request, DateTimeOffset now)
    {
        branch.Kind = request.Kind;
        branch.Status = request.Status;
        branch.AddressLines = [.. request.AddressLines.Select(line => line.Trim())
            .Where(line => line.Length > 0)];
        branch.City = Trimmed(request.City);
        branch.Region = Trimmed(request.Region);
        branch.PostalCode = Trimmed(request.PostalCode);
        branch.Country = Trimmed(request.Country);
        branch.TimeZone = Trimmed(request.TimeZone);
        branch.Phone = Trimmed(request.Phone);
        branch.Email = Trimmed(request.Email);
        branch.ManagerEmployeeId = request.ManagerEmployeeId;
        branch.HeadcountTarget = request.HeadcountTarget;
        branch.OpenedAt = request.OpenedAt;
        branch.UpdatedAt = now;
    }

    // The counts come from the database; the address label is joined afterwards.
    //
    // AddressLines is a jsonb value conversion, so nothing can be done to it in SQL — a
    // string.Join over it does not translate, and asking for one silently costs a
    // client-side evaluation of the whole table in older EF or an exception in this one.
    private IQueryable<Row> Counted(IQueryable<Branch> branches) =>
        branches.AsNoTracking().Select(branch => new Row
        {
            Branch = branch,
            ManagerName = dbContext.Employees
                .Where(employee => employee.Id == branch.ManagerEmployeeId)
                .Select(employee => employee.FirstName + " " + employee.LastName)
                .FirstOrDefault(),

            // Active only. A headcount that counted leavers would overstate every site,
            // and it is the number the target reads against.
            EmployeeCount = dbContext.Employees.Count(employee =>
                employee.BranchId == branch.Id && employee.Status == EmployeeStatus.Active),

            DepartmentCount = dbContext.Employees
                .Where(employee => employee.BranchId == branch.Id
                    && employee.Status == EmployeeStatus.Active
                    && employee.DepartmentId != null)
                .Select(employee => employee.DepartmentId)
                .Distinct()
                .Count(),
        });

    private static BranchDto Describe(Row row) => new()
    {
        Id = row.Branch.Id,
        Name = row.Branch.Name,
        Code = row.Branch.Code,
        Kind = row.Branch.Kind,
        Status = row.Branch.Status,
        AddressLines = row.Branch.AddressLines,
        City = row.Branch.City,
        Region = row.Branch.Region,
        PostalCode = row.Branch.PostalCode,
        Country = row.Branch.Country,
        TimeZone = row.Branch.TimeZone,
        Phone = row.Branch.Phone,
        Email = row.Branch.Email,
        ManagerEmployeeId = row.Branch.ManagerEmployeeId,
        ManagerName = row.ManagerName,
        HeadcountTarget = row.Branch.HeadcountTarget,
        OpenedAt = row.Branch.OpenedAt,
        EmployeeCount = row.EmployeeCount,
        DepartmentCount = row.DepartmentCount,
        AddressLabel = string.Join(
            ", ",
            row.Branch.AddressLines
                .Concat([row.Branch.City, row.Branch.Region, row.Branch.PostalCode, row.Branch.Country])
                .Where(part => !string.IsNullOrWhiteSpace(part))),
    };

    private sealed class Row
    {
        public required Branch Branch { get; init; }

        public string? ManagerName { get; init; }

        public required int EmployeeCount { get; init; }

        public required int DepartmentCount { get; init; }
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
