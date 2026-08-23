using Microsoft.EntityFrameworkCore;
using Motee.Application.Organisation;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Organisation;

internal sealed class DepartmentService(MoteeDbContext dbContext, TimeProvider timeProvider)
    : IDepartmentService
{
    public async Task<IReadOnlyList<DepartmentDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await Project(dbContext.Departments.AsNoTracking().OrderBy(department => department.Name))
            .ToListAsync(cancellationToken);

    public Task<DepartmentDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Project(dbContext.Departments.AsNoTracking().Where(department => department.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<DepartmentResult> CreateAsync(
        DepartmentRequest request,
        CancellationToken cancellationToken = default)
    {
        string name = request.Name.Trim();
        string code = Normalise(request.Code);

        DepartmentOutcome? clash = await FindClashAsync(name, code, null, cancellationToken);

        if (clash is not null)
        {
            return DepartmentResult.Failed(clash.Value);
        }

        Department department = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Code = code,
            Description = Trimmed(request.Description),
            HeadEmployeeId = request.HeadEmployeeId,
            BudgetMonthly = request.BudgetMonthly,
            Status = request.Status,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        // TenantId is stamped by the context from the current tenant.
        dbContext.Departments.Add(department);
        await dbContext.SaveChangesAsync(cancellationToken);

        return DepartmentResult.Ok((await GetAsync(department.Id, cancellationToken))!);
    }

    public async Task<DepartmentResult> UpdateAsync(
        Guid id,
        DepartmentRequest request,
        CancellationToken cancellationToken = default)
    {
        Department? department = await dbContext.Departments
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (department is null)
        {
            return DepartmentResult.Failed(DepartmentOutcome.NotFound);
        }

        string name = request.Name.Trim();
        string code = Normalise(request.Code);

        DepartmentOutcome? clash = await FindClashAsync(name, code, id, cancellationToken);

        if (clash is not null)
        {
            return DepartmentResult.Failed(clash.Value);
        }

        department.Name = name;
        department.Code = code;
        department.Description = Trimmed(request.Description);
        department.HeadEmployeeId = request.HeadEmployeeId;
        department.BudgetMonthly = request.BudgetMonthly;
        department.Status = request.Status;
        department.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return DepartmentResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<DepartmentOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Department? department = await dbContext.Departments
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (department is null)
        {
            return DepartmentOutcome.NotFound;
        }

        // Removing it would leave employees pointing at a department that is gone.
        // Deactivating is the tenant's route for a department that has wound down.
        bool hasEmployees = await dbContext.Employees
            .AnyAsync(employee => employee.DepartmentId == id, cancellationToken);

        if (hasEmployees)
        {
            return DepartmentOutcome.HasEmployees;
        }

        dbContext.Departments.Remove(department);
        await dbContext.SaveChangesAsync(cancellationToken);

        return DepartmentOutcome.Succeeded;
    }

    // Checked in code as well as by the unique indexes so the caller gets which of
    // the two clashed rather than a DbUpdateException.
    private async Task<DepartmentOutcome?> FindClashAsync(
        string name,
        string code,
        Guid? excludingId,
        CancellationToken cancellationToken)
    {
        IQueryable<Department> others = dbContext.Departments
            .Where(department => department.Id != excludingId);

        // ILIKE rather than a StringComparison overload, which has no SQL
        // translation. The name is escaped so a department called "50% Club" is
        // compared literally instead of as a wildcard.
        string pattern = EscapeLike(name);

        if (await others.AnyAsync(
                department => EF.Functions.ILike(department.Name, pattern, @"\"),
                cancellationToken))
        {
            return DepartmentOutcome.DuplicateName;
        }

        if (await others.AnyAsync(department => department.Code == code, cancellationToken))
        {
            return DepartmentOutcome.DuplicateCode;
        }

        return null;
    }

    // Head name and employee count are read through rather than stored: a stored
    // total is wrong the moment someone transfers, and a stored name is wrong the
    // moment they marry.
    private IQueryable<DepartmentDto> Project(IQueryable<Department> query) =>
        query.Select(department => new DepartmentDto
        {
            Id = department.Id,
            Name = department.Name,
            Code = department.Code,
            Description = department.Description,
            HeadEmployeeId = department.HeadEmployeeId,
            HeadName = dbContext.Employees
                .Where(employee => employee.Id == department.HeadEmployeeId)
                .Select(employee => employee.FirstName + " " + employee.LastName)
                .FirstOrDefault(),
            HeadInitials = dbContext.Employees
                .Where(employee => employee.Id == department.HeadEmployeeId)
                .Select(employee => employee.FirstName.Substring(0, 1) + employee.LastName.Substring(0, 1))
                .FirstOrDefault(),
            BudgetMonthly = department.BudgetMonthly,
            Status = department.Status,
            EmployeeCount = dbContext.Employees.Count(employee => employee.DepartmentId == department.Id),
            CreatedAt = department.CreatedAt,
            UpdatedAt = department.UpdatedAt,
        });

    private static string Normalise(string code) => code.Trim().ToUpperInvariant();

    private static string EscapeLike(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
