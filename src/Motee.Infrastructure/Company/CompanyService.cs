using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Company;
using Motee.Application.Countries;
using Motee.Application.Tenancy;
using Motee.Domain.Announcements;
using Motee.Domain.Audit;
using Motee.Domain.Common;
using Motee.Domain.Company;
using Motee.Domain.Countries;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Company;

// The company's own record.
//
// Most of what the profile screen shows is read from somewhere that already
// owns it: the address from the branch marked Headquarters, the headcount from
// the employees, the pinned notice from the announcements module, the org chart
// from reporting lines. What is stored here is only what nothing else holds.
internal sealed class CompanyService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICountryProfileProvider countries,
    IAuditTrail audit,
    TimeProvider timeProvider) : ICompanyService
{
    private const string Module = "organization.company";

    public async Task<CompanyProfileDto> ProfileAsync(
        CancellationToken cancellationToken = default)
    {
        Tenant? tenant = await TenantAsync(tracked: false, cancellationToken);

        if (tenant is null)
        {
            return Empty();
        }

        // The head office, which is where the company's address lives. A
        // company with no branch marked Headquarters has no address to show,
        // and saying so beats inventing one.
        Branch? head = await dbContext.Branches
            .AsNoTracking()
            .Where(branch => branch.Kind == BranchKind.Headquarters
                && branch.Status == BranchStatus.Active)
            .OrderBy(branch => branch.Name)
            .FirstOrDefaultAsync(cancellationToken);

        int headcount = await dbContext.Employees
            .CountAsync(
                employee => EmployeeLifecycle.InPost.Contains(employee.Status),
                cancellationToken);

        return new CompanyProfileDto
        {
            Name = tenant.Name,
            LegalName = tenant.Settings.LegalName,
            Industry = tenant.Industry,
            CountryCode = tenant.CountryCode,
            CountryName = countries.Get(tenant.CountryCode).DisplayName,
            Address = Address(head),
            HeadOfficeBranchId = head?.Id,
            HeadOfficeName = head?.Name,
            ContactEmail = tenant.Settings.SupportEmail ?? tenant.BillingEmail,
            ContactPhone = tenant.Settings.ContactPhone,
            Website = tenant.Settings.Website,
            LogoUrl = tenant.LogoUrl,
            SizeBand = tenant.CompanySize is CompanySize size
                ? CompanySizes.ToLabel(size)
                : null,
            Headcount = headcount,
            RegisteredAt = tenant.CreatedAt,
        };
    }

    public async Task<CompanyResult<CompanyProfileDto>> SaveProfileAsync(
        CompanyProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        Tenant? tenant = await TenantAsync(tracked: true, cancellationToken);

        if (tenant is null)
        {
            return CompanyResult<CompanyProfileDto>.Failed(CompanyOutcome.NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return CompanyResult<CompanyProfileDto>.Refused(
                VerificationRefusal.NumberRequired);
        }

        string was = tenant.Name;

        tenant.Name = request.Name.Trim();

        // Replaced rather than modified: the settings document is a jsonb
        // column whose comparer serialises, so changing it in place leaves EF
        // seeing no change and the save does nothing.
        tenant.Settings = tenant.Settings with
        {
            LegalName = Trimmed(request.LegalName),
            Website = Trimmed(request.Website),
            ContactPhone = Trimmed(request.ContactPhone),
            SupportEmail = Trimmed(request.ContactEmail),
        };

        if (Trimmed(request.Industry) is string industry)
        {
            tenant.Industry = industry;
        }

        audit.Record(
            AuditAction.Update,
            Module,
            string.Equals(was, tenant.Name, StringComparison.Ordinal)
                ? "Company profile updated"
                : $"Company renamed from {was} to {tenant.Name}",
            entityId: tenant.Id,
            entityType: nameof(Tenant));

        await dbContext.SaveChangesAsync(cancellationToken);

        return CompanyResult<CompanyProfileDto>.Ok(await ProfileAsync(cancellationToken));
    }

    public async Task<OrgChartView> OrgChartAsync(
        int? maxLevel = null,
        CancellationToken cancellationToken = default)
    {
        // Only the people in post. A chart carrying leavers draws reporting
        // lines to somebody who left two years ago, and a pending joiner
        // appears before they arrive.
        var rows = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => EmployeeLifecycle.InPost.Contains(employee.Status))
            .Select(employee => new
            {
                employee.Id,
                employee.FirstName,
                employee.LastName,
                employee.PreferredName,
                employee.Initials,
                employee.JobTitle,
                employee.ManagerId,
                Department = employee.DepartmentId == null
                    ? null
                    : dbContext.Departments
                        .Where(department => department.Id == employee.DepartmentId)
                        .Select(department => department.Name)
                        .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        List<OrgCharts.Person> people =
        [
            .. rows.Select(row => new OrgCharts.Person
            {
                Id = row.Id,
                Name = string.IsNullOrWhiteSpace(row.PreferredName)
                    ? $"{row.FirstName} {row.LastName}".Trim()
                    : $"{row.PreferredName} {row.LastName}".Trim(),
                Initials = row.Initials,
                Role = row.JobTitle,
                Department = row.Department,
                ManagerId = row.ManagerId,
            }),
        ];

        return OrgCharts.Build(people, maxLevel);
    }

    public async Task<PinnedNoticeDto?> PinnedNoticeAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        // The most recent pinned notice that is actually live. The screen holds
        // the text in component state, so it survives nothing; this reads the
        // announcement that is already published and pinned.
        Announcement? pinned = await dbContext.Announcements
            .AsNoTracking()
            .Where(announcement => announcement.IsPinned
                && announcement.PublishedAt != null
                && announcement.PublishedAt <= now)
            .OrderByDescending(announcement => announcement.PublishedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (pinned is null)
        {
            return null;
        }

        string? by = pinned.CreatedByUserId is Guid authorId
            ? await dbContext.Users
                .IgnoreQueryFilters()
                .Where(user => user.Id == authorId)
                .Select(user => $"{user.FirstName} {user.LastName}")
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new PinnedNoticeDto
        {
            Id = pinned.Id,
            Title = pinned.Title,
            Body = pinned.Body,
            PublishedAt = pinned.PublishedAt!.Value,
            PublishedBy = by,
        };
    }

    private async Task<Tenant?> TenantAsync(bool tracked, CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        IQueryable<Tenant> source = tracked
            ? dbContext.Tenants
            : dbContext.Tenants.AsNoTracking();

        return await source.FirstOrDefaultAsync(tenant => tenant.Id == tenantId, cancellationToken);
    }

    // Assembled from the branch rather than stored, so it cannot disagree with
    // the site itself.
    private static string? Address(Branch? branch)
    {
        if (branch is null)
        {
            return null;
        }

        List<string> parts =
        [
            .. branch.AddressLines.Where(line => !string.IsNullOrWhiteSpace(line)),
        ];

        foreach (string? part in (string?[])[branch.City, branch.Region, branch.PostalCode, branch.Country])
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                parts.Add(part.Trim());
            }
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private CompanyProfileDto Empty() => new()
    {
        Name = string.Empty,
        CountryCode = CountryCode.Nigeria,
        CountryName = countries.Get(CountryCode.Nigeria).DisplayName,
        Headcount = 0,
    };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
