using Microsoft.EntityFrameworkCore;
using Motee.Application.Assets;
using Motee.Application.Common;
using Motee.Domain.Assets;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Assets;

internal sealed class AssetService(MoteeDbContext dbContext, TimeProvider timeProvider) : IAssetService
{
    public async Task<PagedResult<AssetDto>> ListAsync(
        AssetQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Asset> matching = Filter(query);

        int total = await matching.CountAsync(cancellationToken);

        List<AssetDto> items = await Project(matching)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AssetDto>
        {
            Items = items,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public Task<AssetDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Project(dbContext.Assets.AsNoTracking().Where(asset => asset.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<AssetResult> CreateAsync(
        AssetRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await TagTakenAsync(request.Tag, null, cancellationToken))
        {
            return AssetResult.Failed(AssetOutcome.DuplicateTag);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Asset asset = new()
        {
            Id = Guid.NewGuid(),
            Tag = request.Tag.Trim(),
            Name = request.Name.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apply(asset, request);

        dbContext.Assets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken);

        return AssetResult.Ok((await GetAsync(asset.Id, cancellationToken))!);
    }

    public async Task<AssetResult> UpdateAsync(
        Guid id,
        AssetRequest request,
        CancellationToken cancellationToken = default)
    {
        Asset? asset = await FindAsync(id, cancellationToken);

        if (asset is null)
        {
            return AssetResult.Failed(AssetOutcome.NotFound);
        }

        if (await TagTakenAsync(request.Tag, id, cancellationToken))
        {
            return AssetResult.Failed(AssetOutcome.DuplicateTag);
        }

        Apply(asset, request);
        asset.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return AssetResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<AssetResult> AssignAsync(
        Guid id,
        AssignAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        Asset? asset = await FindAsync(id, cancellationToken);

        if (asset is null)
        {
            return AssetResult.Failed(AssetOutcome.NotFound);
        }

        AssetOutcome? problem = await CheckAssignableAsync(asset, request.EmployeeId, cancellationToken);

        if (problem is not null)
        {
            return AssetResult.Failed(problem.Value);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        asset.AssignedToEmployeeId = request.EmployeeId;
        asset.AssignedDate = request.AssignedDate ?? DateOnly.FromDateTime(now.UtcDateTime);
        asset.Status = AssetStatus.Assigned;
        asset.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return AssetResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<AssetResult> ReturnAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Asset? asset = await FindAsync(id, cancellationToken);

        if (asset is null)
        {
            return AssetResult.Failed(AssetOutcome.NotFound);
        }

        if (!AssetLifecycle.CanMove(asset.Status, AssetStatus.Available))
        {
            return AssetResult.Failed(AssetOutcome.InvalidStatusChange);
        }

        // Cleared rather than kept as "last holder". Without an assignment history
        // there is nowhere honest to put that, and a stale name on an available asset
        // reads as though they still have it.
        asset.AssignedToEmployeeId = null;
        asset.AssignedDate = null;
        asset.Status = AssetStatus.Available;
        asset.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return AssetResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<AssetResult> ChangeStatusAsync(
        Guid id,
        AssetStatus status,
        CancellationToken cancellationToken = default)
    {
        Asset? asset = await FindAsync(id, cancellationToken);

        if (asset is null)
        {
            return AssetResult.Failed(AssetOutcome.NotFound);
        }

        if (!AssetLifecycle.CanMove(asset.Status, status))
        {
            return AssetResult.Failed(AssetOutcome.InvalidStatusChange);
        }

        // Nobody is holding a lost or retired asset, so the assignment goes with it.
        if (status is AssetStatus.Lost or AssetStatus.Retired or AssetStatus.Available)
        {
            asset.AssignedToEmployeeId = null;
            asset.AssignedDate = null;
        }

        asset.Status = status;
        asset.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return AssetResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<AssetOutcome?> StageForEmployeeAsync(
        Guid employeeId,
        IReadOnlyList<AssetRequest> assets,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (AssetRequest request in assets)
        {
            if (await TagTakenAsync(request.Tag, null, cancellationToken))
            {
                return AssetOutcome.DuplicateTag;
            }

            // Two rows in the same wizard submission carrying one tag would both pass
            // the database check, which only sees what is committed.
            if (dbContext.ChangeTracker.Entries<Asset>().Any(entry =>
                    string.Equals(
                        entry.Entity.Tag, request.Tag.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return AssetOutcome.DuplicateTag;
            }

            Asset asset = new()
            {
                Id = Guid.NewGuid(),
                Tag = request.Tag.Trim(),
                Name = request.Name.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
            };

            Apply(asset, request);

            asset.AssignedToEmployeeId = employeeId;
            asset.AssignedDate = request.AssignedDate ?? DateOnly.FromDateTime(now.UtcDateTime);
            asset.Status = AssetStatus.Assigned;

            // Added, not saved: the caller owns the transaction so the employee and
            // their kit commit together.
            dbContext.Assets.Add(asset);
        }

        return null;
    }

    private async Task<AssetOutcome?> CheckAssignableAsync(
        Asset asset,
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        if (!AssetLifecycle.CanMove(asset.Status, AssetStatus.Assigned))
        {
            return AssetOutcome.InvalidStatusChange;
        }

        // Reassigning takes a return first, or two people are both holding it.
        if (asset.AssignedToEmployeeId is Guid holder && holder != employeeId)
        {
            return AssetOutcome.AlreadyAssigned;
        }

        bool employeeExists = await dbContext.Employees
            .AnyAsync(employee => employee.Id == employeeId, cancellationToken);

        return employeeExists ? null : AssetOutcome.UnknownEmployee;
    }

    private Task<Asset?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Assets.FirstOrDefaultAsync(asset => asset.Id == id, cancellationToken);

    // Scoped to the tenant, not global: two companies may both label something
    // "AST-0001".
    private Task<bool> TagTakenAsync(string tag, Guid? excludingId, CancellationToken cancellationToken)
    {
        string trimmed = tag.Trim();

        return dbContext.Assets.AnyAsync(
            asset => asset.Tag.ToUpper() == trimmed.ToUpper(null) && asset.Id != excludingId,
            cancellationToken);
    }

    private static void Apply(Asset asset, AssetRequest request)
    {
        asset.Tag = request.Tag.Trim();
        asset.Name = request.Name.Trim();
        asset.Category = Trimmed(request.Category);
        asset.SerialNumber = Trimmed(request.SerialNumber);
        asset.Notes = Trimmed(request.Notes);
    }

    private IQueryable<Asset> Filter(AssetQuery query)
    {
        IQueryable<Asset> assets = Narrow(dbContext.Assets.AsNoTracking(), query);

        if (query.AssignedToEmployeeId is Guid holder)
        {
            assets = assets.Where(asset => asset.AssignedToEmployeeId == holder);
        }

        if (query.Status is AssetStatus status)
        {
            assets = assets.Where(asset => asset.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            string category = query.Category.Trim();

            assets = assets.Where(asset => asset.Category != null && asset.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{EscapeLike(query.Search.Trim())}%";

            assets = assets.Where(asset =>
                EF.Functions.ILike(asset.Tag, pattern, @"\")
                || EF.Functions.ILike(asset.Name, pattern, @"\")
                || (asset.SerialNumber != null
                    && EF.Functions.ILike(asset.SerialNumber, pattern, @"\")));
        }

        return assets;
    }

    // Assets are company property rather than people, so Department and Team do not
    // divide them the way they divide employees — anyone trusted with the module sees
    // the estate. Self is the exception: it is what self-service grants, and it means
    // "the kit I am holding".
    private static IQueryable<Asset> Narrow(IQueryable<Asset> assets, AssetQuery query) =>
        query.Scope.Kind switch
        {
            // Assets are company property rather than people, so the people-shaped
            // scopes do not divide them: anyone trusted with the module sees the
            // estate, however narrowly they see employees.
            DataScopeKind.All
                or DataScopeKind.Department
                or DataScopeKind.BusinessUnit
                or DataScopeKind.DirectReports => assets,

            // Self is the exception, and it is what self-service grants: the kit the
            // person is holding.
            DataScopeKind.Self => assets.Where(asset =>
                asset.AssignedToEmployeeId != null
                && asset.AssignedToEmployeeId == query.ViewerEmployeeId),

            _ => assets.Where(_ => false),
        };

    private IQueryable<AssetDto> Project(IQueryable<Asset> assets) =>
        assets
            .OrderBy(asset => asset.Tag)
            .Select(asset => new AssetDto
            {
                Id = asset.Id,
                Tag = asset.Tag,
                Name = asset.Name,
                Category = asset.Category,
                SerialNumber = asset.SerialNumber,
                AssignedToEmployeeId = asset.AssignedToEmployeeId,
                AssignedToName = dbContext.Employees
                    .Where(employee => employee.Id == asset.AssignedToEmployeeId)
                    .Select(employee => employee.FirstName + " " + employee.LastName)
                    .FirstOrDefault(),
                AssignedDate = asset.AssignedDate,
                Notes = asset.Notes,
                Status = asset.Status,
                CreatedAt = asset.CreatedAt,
                UpdatedAt = asset.UpdatedAt,
            });

    private static string EscapeLike(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
