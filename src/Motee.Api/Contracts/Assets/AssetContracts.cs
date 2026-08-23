using Motee.Domain.Assets;

namespace Motee.Api.Contracts.Assets;

// Bound from the query string, so the list takes one record rather than a row of
// positional parameters.
public sealed record AssetFilters
{
    public string? Search { get; init; }

    public string? Category { get; init; }

    public AssetStatus? Status { get; init; }

    // Behind "what is this person holding" on the employee detail page.
    public Guid? AssignedToEmployeeId { get; init; }
}

public sealed record ChangeAssetStatusRequest
{
    public required AssetStatus Status { get; init; }
}
