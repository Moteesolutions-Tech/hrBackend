using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Platform;
using Motee.Domain.Platform;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Platform;

internal sealed class PlatformService(MoteeDbContext dbContext) : IPlatformService
{
    public async Task<PagedResult<PlatformTenantDto>> ListTenantsAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Domain.Tenants.Tenant> tenants = dbContext.Tenants.AsNoTracking();

        int total = await tenants.CountAsync(cancellationToken);

        List<PlatformTenantDto> items = await Project(tenants)
            .OrderByDescending(tenant => tenant.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<PlatformTenantDto>
        {
            Items = items,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<PlatformTenantDto?> GetTenantAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        await Project(dbContext.Tenants.AsNoTracking().Where(tenant => tenant.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PlatformStaffDto>> ListStaffAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsPlatformStaff && user.PlatformRole != null)
            .OrderBy(user => user.Email)
            .Select(user => new PlatformStaffDto
            {
                UserId = user.Id,
                Email = user.Email!,
                Name = user.FirstName + " " + user.LastName,
                Role = user.PlatformRole!.Value,
            })
            .ToListAsync(cancellationToken);

    public async Task<PlatformStaffResult> GrantAsync(
        string email,
        PlatformRole role,
        CancellationToken cancellationToken = default)
    {
        string normalised = email.Trim().ToUpperInvariant();

        ApplicationUser? user = await dbContext.Users
            .FirstOrDefaultAsync(candidate => candidate.NormalizedEmail == normalised, cancellationToken);

        if (user is null)
        {
            return PlatformStaffResult.Failed(PlatformStaffOutcome.NotFound);
        }

        // Somebody cannot be both a company's HR admin and a Motee operator. The token
        // builder refuses to represent that combination at all, so an account promoted
        // here would simply stop being able to sign in — a worse failure than refusing.
        if (user.TenantId is not null)
        {
            return PlatformStaffResult.Failed(PlatformStaffOutcome.BelongsToTenant);
        }

        user.IsPlatformStaff = true;
        user.PlatformRole = role;

        await dbContext.SaveChangesAsync(cancellationToken);

        return PlatformStaffResult.Ok(new PlatformStaffDto
        {
            UserId = user.Id,
            Email = user.Email!,
            Name = $"{user.FirstName} {user.LastName}",
            Role = role,
        });
    }

    public async Task<PlatformStaffOutcome> RevokeAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ApplicationUser? user = await dbContext.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null || !user.IsPlatformStaff)
        {
            return PlatformStaffOutcome.NotFound;
        }

        // Removing the last administrator would leave nobody able to grant it back
        // without opening the database. Support and Finance can be removed freely.
        if (user.PlatformRole == PlatformRole.Admin)
        {
            int administrators = await dbContext.Users.CountAsync(
                candidate => candidate.IsPlatformStaff
                    && candidate.PlatformRole == PlatformRole.Admin,
                cancellationToken);

            if (administrators <= 1)
            {
                return PlatformStaffOutcome.LastAdministrator;
            }
        }

        // Both cleared together. The flag alone would leave somebody outside every tenant
        // and authorised for nothing, which reads as a permissions bug.
        user.IsPlatformStaff = false;
        user.PlatformRole = null;

        await dbContext.SaveChangesAsync(cancellationToken);

        return PlatformStaffOutcome.Succeeded;
    }

    private IQueryable<PlatformTenantDto> Project(IQueryable<Domain.Tenants.Tenant> tenants) =>
        tenants.Select(tenant => new PlatformTenantDto
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Slug = tenant.Slug,
            Plan = tenant.Plan,
            Status = tenant.Status,
            CountryCode = tenant.CountryCode.Value,
            BillingEmail = tenant.BillingEmail,

            // Users are outside the tenant filter already; employees are inside it, and
            // platform staff carry no tenant — so without IgnoreQueryFilters this would
            // count zero for every company and look like nobody uses the product.
            Users = dbContext.Users.Count(user => user.TenantId == tenant.Id),
            Employees = dbContext.Employees
                .IgnoreQueryFilters()
                .Count(employee => employee.TenantId == tenant.Id),
            CreatedAt = tenant.CreatedAt,
            TrialEndsAt = tenant.TrialEndsAt,
            OnboardingCompletedAt = tenant.OnboardingCompletedAt,
        });
}
