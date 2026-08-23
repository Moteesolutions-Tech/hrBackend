using Motee.Domain.Common;

namespace Motee.Domain.Assets;

public enum AssetStatus
{
    // On the books, nobody holding it.
    Available,

    Assigned,

    // Reported missing. Kept rather than deleted: an audit asks what happened to it,
    // and a laptop that turns up needs somewhere to come back to.
    Lost,

    // End of life — sold, scrapped or written off. Terminal.
    Retired,
}

public class Asset : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The tenant's own label, e.g. "AST-0142". Unique within the tenant, and the
    // thing printed on the sticker someone reads off the underside of a laptop.
    public required string Tag { get; set; }

    public required string Name { get; set; }

    // Free text on purpose. "Laptop" and "Phone" are the obvious ones, but a company
    // vehicle, a uniform and a desk are all assets, and no fixed list survives that.
    public string? Category { get; set; }

    // From the manufacturer, not from us. Distinct from Tag, which is the company's.
    public string? SerialNumber { get; set; }

    public Guid? AssignedToEmployeeId { get; set; }

    public DateOnly? AssignedDate { get; set; }

    public string? Notes { get; set; }

    public AssetStatus Status { get; set; } = AssetStatus.Available;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// Which status changes are legitimate. Without this an asset can be assigned while
// retired, or quietly moved out of Lost with nothing recording that it turned up.
public static class AssetLifecycle
{
    private static readonly Dictionary<AssetStatus, AssetStatus[]> Allowed = new()
    {
        [AssetStatus.Available] = [AssetStatus.Assigned, AssetStatus.Lost, AssetStatus.Retired],

        // Returning goes to Available. Retiring straight from Assigned covers the
        // laptop that dies while someone is holding it.
        [AssetStatus.Assigned] = [AssetStatus.Available, AssetStatus.Lost, AssetStatus.Retired],

        // It turned up, or it is written off.
        [AssetStatus.Lost] = [AssetStatus.Available, AssetStatus.Retired],

        // Scrapped is scrapped. Bringing one back is a new record, not a status change.
        [AssetStatus.Retired] = [],
    };

    public static bool CanMove(AssetStatus from, AssetStatus to) =>
        from == to || (Allowed.TryGetValue(from, out AssetStatus[]? allowed) && allowed.Contains(to));
}
