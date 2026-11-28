using Motee.Application.Common;
using Motee.Domain.Assets;
using Motee.Domain.Authorization;

namespace Motee.Application.Assets;

public interface IAssetService
{
    Task<PagedResult<AssetDto>> ListAsync(
        AssetQuery query,
        CancellationToken cancellationToken = default);

    Task<AssetDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AssetResult> CreateAsync(AssetRequest request, CancellationToken cancellationToken = default);

    Task<AssetResult> UpdateAsync(
        Guid id,
        AssetRequest request,
        CancellationToken cancellationToken = default);

    // Assign and return are separate from Update for the same reason a status change
    // is separate on an employee: handing someone a laptop is an act, not a side
    // effect of correcting its serial number.
    // What the signed-in employee is holding. A separate shape rather than the register
    // filtered, for the same reason the occupational-health views are separate methods:
    // the HR row carries notes that are HR's — "screen cracked, consider a deduction" —
    // and a narrow DTO has nowhere to put them, so the mistake cannot compile.
    //
    // Empty for an account with no employee record, which the owner permanently is.
    Task<IReadOnlyList<MyAssetDto>> MineAsync(CancellationToken cancellationToken = default);

    Task<AssetResult> AssignAsync(
        Guid id,
        AssignAssetRequest request,
        CancellationToken cancellationToken = default);

    Task<AssetResult> ReturnAsync(
        Guid id,
        ReturnAssetRequest? request = null,
        CancellationToken cancellationToken = default);

    // Every spell somebody has held this asset, newest first. The question an audit asks,
    // and the one the asset row itself cannot answer — it holds only who has it now.
    Task<IReadOnlyList<AssetAssignmentDto>> HistoryAsync(
        Guid assetId,
        CancellationToken cancellationToken = default);

    Task<AssetResult> ChangeStatusAsync(
        Guid id,
        AssetStatus status,
        CancellationToken cancellationToken = default);

    // Used by the employee wizard, which hands over a laptop as part of onboarding.
    // Adds to the context without saving, so the whole form commits together.
    Task<AssetOutcome?> StageForEmployeeAsync(
        Guid employeeId,
        IReadOnlyList<AssetRequest> assets,
        CancellationToken cancellationToken = default);
}

public enum AssetOutcome
{
    Succeeded,
    NotFound,
    DuplicateTag,
    UnknownEmployee,

    // Already held by someone else. Reassigning takes a return first, so two people
    // cannot both be holding the same machine.
    AlreadyAssigned,

    // Assigning a retired asset, or moving one out of Retired.
    InvalidStatusChange,
}

public sealed record AssetQuery : PagedQuery
{
    public required DataScope Scope { get; init; }

    // The employee record of the person asking. Self scope shows them what they hold.
    public Guid? ViewerEmployeeId { get; init; }

    public string? Search { get; init; }

    public string? Category { get; init; }

    public AssetStatus? Status { get; init; }

    // Narrowing by physical state: "show me everything damaged" is the question the
    // replacement budget starts from.
    public AssetCondition? Condition { get; init; }

    public Guid? AssignedToEmployeeId { get; init; }
}

public sealed record AssetRequest
{
    public required string Tag { get; init; }

    public required string Name { get; init; }

    public string? Category { get; init; }

    public string? SerialNumber { get; init; }

    public string? Notes { get; init; }

    // The state it is in, and what it cost. Both optional: an asset whose condition
    // nobody has judged, or which was never priced, is still an asset to record.
    public AssetCondition? Condition { get; init; }

    public decimal? Value { get; init; }

    // Only read when the asset is created through the employee wizard, where the
    // holder is the person being onboarded.
    public DateOnly? AssignedDate { get; init; }
}

public sealed record AssignAssetRequest
{
    public required Guid EmployeeId { get; init; }

    public DateOnly? AssignedDate { get; init; }

    // What state it was in when handed over. Paired with the condition on return, this is
    // what supports a deduction or a write-off; either on its own proves nothing.
    public string? Condition { get; init; }
}

public sealed record ReturnAssetRequest
{
    public DateOnly? ReturnedOn { get; init; }

    public string? Reason { get; init; }

    public string? Condition { get; init; }
}

public sealed record AssetAssignmentDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required DateOnly AssignedOn { get; init; }

    public DateOnly? ReturnedOn { get; init; }

    public string? ReturnReason { get; init; }

    public string? ConditionOnAssign { get; init; }

    public string? ConditionOnReturn { get; init; }

    // Null while they still hold it, so a screen can show "142 days" or "since 3 June"
    // without working out which case it is in.
    public int? HeldDays { get; init; }

    public required bool IsOpen { get; init; }
}

public sealed record AssetResult
{
    public required AssetOutcome Outcome { get; init; }

    public AssetDto? Asset { get; init; }

    public bool Succeeded => Outcome == AssetOutcome.Succeeded;

    public static AssetResult Failed(AssetOutcome outcome) => new() { Outcome = outcome };

    public static AssetResult Ok(AssetDto asset) =>
        new() { Outcome = AssetOutcome.Succeeded, Asset = asset };
}

// One asset, as the person holding it sees it.
//
// No Notes, which is the point: that field is HR's record about the item, and the
// condition on handover is what supports a deduction. No AssignedToName either — on this
// list it is always the caller.
public sealed record MyAssetDto
{
    public required Guid Id { get; init; }

    public required string Tag { get; init; }

    public required string Name { get; init; }

    public string? Category { get; init; }

    public string? SerialNumber { get; init; }

    public DateOnly? AssignedDate { get; init; }

    public required AssetStatus Status { get; init; }
}

public sealed record AssetDto
{
    public required Guid Id { get; init; }

    public required string Tag { get; init; }

    public required string Name { get; init; }

    public string? Category { get; init; }

    public string? SerialNumber { get; init; }

    public AssetCondition? Condition { get; init; }

    public decimal? Value { get; init; }

    public Guid? AssignedToEmployeeId { get; init; }

    // Resolved here so the assets table does not need a lookup per row.
    public string? AssignedToName { get; init; }

    public DateOnly? AssignedDate { get; init; }

    public string? Notes { get; init; }

    public required AssetStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
