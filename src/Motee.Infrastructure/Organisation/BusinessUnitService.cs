using Microsoft.EntityFrameworkCore;
using Motee.Application.Organisation;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Organisation;

internal sealed class BusinessUnitService(MoteeDbContext dbContext, TimeProvider timeProvider)
    : IBusinessUnitService
{
    public async Task<IReadOnlyList<BusinessUnitDto>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await Project(dbContext.BusinessUnits.AsNoTracking().OrderBy(unit => unit.Name))
            .ToListAsync(cancellationToken);

    public Task<BusinessUnitDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Project(dbContext.BusinessUnits.AsNoTracking().Where(unit => unit.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<BusinessUnitResult> CreateAsync(
        BusinessUnitRequest request,
        CancellationToken cancellationToken = default)
    {
        string name = request.Name.Trim();

        if (await NameTakenAsync(name, null, cancellationToken))
        {
            return BusinessUnitResult.Failed(BusinessUnitOutcome.DuplicateName);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        BusinessUnit unit = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Code = Normalise(request.Code),
            Description = Trimmed(request.Description),
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // TenantId is stamped by the context from the current tenant.
        dbContext.BusinessUnits.Add(unit);
        await dbContext.SaveChangesAsync(cancellationToken);

        return BusinessUnitResult.Ok((await GetAsync(unit.Id, cancellationToken))!);
    }

    public async Task<BusinessUnitResult> UpdateAsync(
        Guid id,
        BusinessUnitRequest request,
        CancellationToken cancellationToken = default)
    {
        BusinessUnit? unit = await dbContext.BusinessUnits
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (unit is null)
        {
            return BusinessUnitResult.Failed(BusinessUnitOutcome.NotFound);
        }

        string name = request.Name.Trim();

        if (await NameTakenAsync(name, id, cancellationToken))
        {
            return BusinessUnitResult.Failed(BusinessUnitOutcome.DuplicateName);
        }

        // Renaming is safe on purpose: access levels reference the id, so a unit can be
        // renamed without changing who any level reaches. That is the reason this is an
        // entity rather than a label on a department.
        unit.Name = name;
        unit.Code = Normalise(request.Code);
        unit.Description = Trimmed(request.Description);
        unit.IsActive = request.IsActive;
        unit.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return BusinessUnitResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<BusinessUnitOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        BusinessUnit? unit = await dbContext.BusinessUnits
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (unit is null)
        {
            return BusinessUnitOutcome.NotFound;
        }

        // The FK from departments is SET NULL, so deleting would silently detach them
        // and every access level scoped to this unit would stop reaching their people —
        // a permission change nobody requested and nothing records.
        bool hasDepartments = await dbContext.Departments
            .AnyAsync(department => department.BusinessUnitId == id, cancellationToken);

        if (hasDepartments)
        {
            return BusinessUnitOutcome.HasDepartments;
        }

        dbContext.BusinessUnits.Remove(unit);
        await dbContext.SaveChangesAsync(cancellationToken);

        return BusinessUnitOutcome.Succeeded;
    }

    // Checked in code as well as by ix_business_units_tenant_name so the caller gets a
    // named outcome rather than a DbUpdateException.
    private async Task<bool> NameTakenAsync(
        string name,
        Guid? excludingId,
        CancellationToken cancellationToken)
    {
        // ILIKE rather than a StringComparison overload, which has no SQL translation.
        // Escaped so a unit called "R&D 100%" is compared literally, not as a wildcard.
        string pattern = EscapeLike(name);

        return await dbContext.BusinessUnits
            .Where(unit => unit.Id != excludingId)
            .AnyAsync(unit => EF.Functions.ILike(unit.Name, pattern, @"\"), cancellationToken);
    }

    private IQueryable<BusinessUnitDto> Project(IQueryable<BusinessUnit> query) =>
        query.Select(unit => new BusinessUnitDto
        {
            Id = unit.Id,
            Name = unit.Name,
            Code = unit.Code,
            Description = unit.Description,
            IsActive = unit.IsActive,
            DepartmentCount = dbContext.Departments
                .Count(department => department.BusinessUnitId == unit.Id),
            CreatedAt = unit.CreatedAt,
            UpdatedAt = unit.UpdatedAt,
        });

    private static string? Normalise(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    private static string EscapeLike(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
