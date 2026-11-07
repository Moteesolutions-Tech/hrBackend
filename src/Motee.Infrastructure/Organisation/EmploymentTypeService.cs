using Microsoft.EntityFrameworkCore;
using Motee.Application.Organisation;
using Motee.Application.Tenancy;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Organisation;

// Employment types and their configuration.
//
// The employee count is derived, and it is the figure that gates retiring a
// type — so it is counted rather than kept on the row. A stored count that
// drifts would let somebody retire a type three people are employed under.
internal sealed class EmploymentTypeService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IEmploymentTypeService
{
    public async Task<IReadOnlyList<EmploymentTypeDto>> ListAsync(
        bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        List<EmploymentTypeConfig> types = await dbContext.EmploymentTypeConfigs
            .AsNoTracking()
            .Where(type => includeInactive || type.IsActive)
            .OrderBy(type => type.Sequence)
            .ThenBy(type => type.Name)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(types, cancellationToken);
    }

    public async Task<EmploymentTypeDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EmploymentTypeConfig? type = await dbContext.EmploymentTypeConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return type is null ? null : (await DescribeAsync([type], cancellationToken))[0];
    }

    public async Task<EmploymentTypeResult> CreateAsync(
        EmploymentTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await NameTakenAsync(request.Name, null, cancellationToken))
        {
            return EmploymentTypeResult.Refused(EmploymentTypeRefusal.NameTaken);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        EmploymentTypeConfig type = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Name = string.Empty,

            // After the built-ins, so a tenant's own additions sort below the
            // eleven the product recognises.
            Sequence = 100 + await dbContext.EmploymentTypeConfigs
                .CountAsync(row => row.Kind == null, cancellationToken),
            CreatedAt = now,
        };

        Apply(type, request, now);

        EmploymentTypeRefusal refusal = EmploymentTypeConfigs.Refusal(type);

        if (refusal != EmploymentTypeRefusal.None)
        {
            return EmploymentTypeResult.Refused(refusal);
        }

        dbContext.EmploymentTypeConfigs.Add(type);

        await dbContext.SaveChangesAsync(cancellationToken);

        return EmploymentTypeResult.Ok((await DescribeAsync([type], cancellationToken))[0]);
    }

    public async Task<EmploymentTypeResult> UpdateAsync(
        Guid id,
        EmploymentTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        EmploymentTypeConfig? type = await FindAsync(id, cancellationToken);

        if (type is null)
        {
            return EmploymentTypeResult.Failed(EmploymentTypeOutcome.NotFound);
        }

        if (await NameTakenAsync(request.Name, id, cancellationToken))
        {
            return EmploymentTypeResult.Refused(EmploymentTypeRefusal.NameTaken);
        }

        Apply(type, request, timeProvider.GetUtcNow());

        EmploymentTypeRefusal refusal = EmploymentTypeConfigs.Refusal(type);

        if (refusal != EmploymentTypeRefusal.None)
        {
            return EmploymentTypeResult.Refused(refusal);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return EmploymentTypeResult.Ok((await DescribeAsync([type], cancellationToken))[0]);
    }

    public async Task<EmploymentTypeResult> DeactivateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EmploymentTypeConfig? type = await FindAsync(id, cancellationToken);

        if (type is null)
        {
            return EmploymentTypeResult.Failed(EmploymentTypeOutcome.NotFound);
        }

        // Retiring a type people are on would leave their records reading
        // against something the company says it no longer uses. Move them
        // first, which is a decision about people rather than configuration.
        if (await CountAsync(type, cancellationToken) > 0)
        {
            return EmploymentTypeResult.Refused(EmploymentTypeRefusal.InUse);
        }

        type.IsActive = false;
        type.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return EmploymentTypeResult.Ok((await DescribeAsync([type], cancellationToken))[0]);
    }

    public async Task<EmploymentTypeResult> ReactivateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EmploymentTypeConfig? type = await FindAsync(id, cancellationToken);

        if (type is null)
        {
            return EmploymentTypeResult.Failed(EmploymentTypeOutcome.NotFound);
        }

        type.IsActive = true;
        type.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return EmploymentTypeResult.Ok((await DescribeAsync([type], cancellationToken))[0]);
    }

    public async Task<EmploymentTypeOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EmploymentTypeConfig? type = await FindAsync(id, cancellationToken);

        if (type is null)
        {
            return EmploymentTypeOutcome.NotFound;
        }

        // The eleven stay. Recruitment, onboarding, payroll and the reports
        // all map onto them, and a tenant deleting one would break screens
        // that have nothing to do with this page.
        if (type.Kind is not null || await CountAsync(type, cancellationToken) > 0)
        {
            return EmploymentTypeOutcome.Refused;
        }

        dbContext.EmploymentTypeConfigs.Remove(type);

        await dbContext.SaveChangesAsync(cancellationToken);

        return EmploymentTypeOutcome.Succeeded;
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<EmploymentTypeConfig?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.EmploymentTypeConfigs.FirstOrDefaultAsync(
            row => row.Id == id, cancellationToken);

    // Two types with one name is two answers to "which is Sarah on".
    private async Task<bool> NameTakenAsync(
        string name,
        Guid? ignoring,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // ILike rather than a lowered comparison: it is what Postgres can
        // actually index, and the analyzer is right that a culture-less
        // ToLower would be wrong here. The wildcards are escaped because a
        // type legitimately named "Contract (100%)" would otherwise match
        // every other type.
        string pattern = name.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return await dbContext.EmploymentTypeConfigs.AnyAsync(
            row => row.Id != ignoring && EF.Functions.ILike(row.Name, pattern, "\\"),
            cancellationToken);
    }

    private static void Apply(
        EmploymentTypeConfig type,
        EmploymentTypeRequest request,
        DateTimeOffset now)
    {
        type.Name = request.Name?.Trim() ?? string.Empty;
        type.Description = Blank(request.Description);
        type.PayFrequency = request.PayFrequency;
        type.ContractDuration = request.ContractDuration;
        type.DefaultLeaveDays = request.DefaultLeaveDays;
        type.ProbationMonths = request.ProbationMonths;
        type.ProbationReviewRequired = request.ProbationReviewRequired;
        type.HoursPerWeek = request.HoursPerWeek;
        type.FlexibleHours = request.FlexibleHours;
        type.PayrollInclusion = request.PayrollInclusion;
        type.EligibleForBenefits = request.EligibleForBenefits;
        type.PensionEmployeePercent = request.PensionEmployeePercent;
        type.PensionEmployerPercent = request.PensionEmployerPercent;

        // Replaced rather than mutated: the jsonb converter compares by
        // serialising, so an in-place edit is not seen as a change.
        type.Benefits = [.. request.Benefits ?? []];
        type.StatutoryDeductionOverrides = [.. request.StatutoryDeductionOverrides ?? []];
        type.UpdatedAt = now;
    }

    // People currently employed under a type. A tenant's own type is matched
    // by nothing yet — employees carry the built-in kind — so a custom type
    // counts nobody until employees can point at one.
    private async Task<int> CountAsync(
        EmploymentTypeConfig type,
        CancellationToken cancellationToken) =>
        type.Kind is EmploymentType kind
            ? await dbContext.Employees.CountAsync(
                employee => employee.EmploymentType == kind
                    && employee.Status != EmployeeStatus.Deleted,
                cancellationToken)
            : 0;

    private async Task<List<EmploymentTypeDto>> DescribeAsync(
        IReadOnlyList<EmploymentTypeConfig> types,
        CancellationToken cancellationToken)
    {
        if (types.Count == 0)
        {
            return [];
        }

        // One grouped count rather than one query per type.
        Dictionary<EmploymentType, int> counts = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Status != EmployeeStatus.Deleted
                && employee.EmploymentType != null)
            .GroupBy(employee => employee.EmploymentType!.Value)
            .Select(group => new { Kind = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Kind, row => row.Count, cancellationToken);

        CountryCode country = await CountryAsync(cancellationToken);

        return
        [
            .. types.Select(type => new EmploymentTypeDto
            {
                Id = type.Id,
                Kind = type.Kind,
                Name = type.Name,
                Description = type.Description,
                PayFrequency = type.PayFrequency,
                ContractDuration = type.ContractDuration,
                DefaultLeaveDays = type.DefaultLeaveDays,
                ProbationMonths = type.ProbationMonths,
                ProbationReviewRequired = type.ProbationReviewRequired,
                HoursPerWeek = type.HoursPerWeek,
                FlexibleHours = type.FlexibleHours,
                PayrollInclusion = type.PayrollInclusion,
                EligibleForBenefits = type.EligibleForBenefits,
                PensionEmployeePercent = type.PensionEmployeePercent,
                PensionEmployerPercent = type.PensionEmployerPercent,
                Benefits = type.Benefits,
                StatutoryDeductions = EmploymentTypeConfigs.DeductionsFor(type, country),
                DeductionsOverridden = type.StatutoryDeductionOverrides.Count > 0,
                IsActive = type.IsActive,
                EmployeeCount = type.Kind is EmploymentType kind
                    ? counts.GetValueOrDefault(kind)
                    : 0,
                // Only a tenant's own addition. The eleven built-in kinds
                // stay, because recruitment, onboarding and the reports all
                // map onto them.
                CanDelete = type.Kind is null,
                UpdatedAt = type.UpdatedAt,
            }),
        ];
    }

    // Scoped by id, because tenants are not themselves tenant-scoped — an
    // unfiltered read returns some arbitrary company's row and would attach
    // Nigeria's deductions to a UK payroll.
    private async Task<CountryCode> CountryAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return CountryCode.Nigeria;
        }

        Motee.Domain.Tenants.Tenant? tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == tenantId, cancellationToken);

        return tenant?.CountryCode ?? CountryCode.Nigeria;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
