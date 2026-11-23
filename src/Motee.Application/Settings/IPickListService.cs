using Motee.Domain.Settings;

namespace Motee.Application.Settings;

// The dropdown options a company configures.
//
// Three of the five lists the screen offers are already tables — departments,
// positions and branches. Those are served from their own rows and refuse
// writes here, naming the screen that owns them: a second list of departments
// would be a list the employee form never reads, so adding one in Settings
// would look like it worked and change nothing.
public interface IPickListService
{
    Task<IReadOnlyList<PickListView>> ListAsync(CancellationToken cancellationToken = default);

    Task<PickListView?> FindAsync(string key, CancellationToken cancellationToken = default);

    Task<PickListResult> AddAsync(
        string key,
        PickListValueRequest request,
        CancellationToken cancellationToken = default);

    Task<PickListResult> UpdateAsync(
        string key,
        Guid id,
        PickListValueRequest request,
        CancellationToken cancellationToken = default);

    Task<PickListResult> RemoveAsync(
        string key,
        Guid id,
        CancellationToken cancellationToken = default);

    Task<PickListResult> SetDefaultAsync(
        string key,
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed record PickListView
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required IReadOnlyList<PickListOptionDto> Options { get; init; }

    // Whether the dialog should open at all. False on a list that is already a
    // table of its own.
    public required bool Editable { get; init; }

    // Where it is edited instead. Null on an editable list.
    public string? OwnedBy { get; init; }

    public string? OwnerRoute { get; init; }
}

public sealed record PickListOptionDto
{
    public required Guid Id { get; init; }

    public required string Value { get; init; }

    public required bool IsDefault { get; init; }
}

public sealed record PickListValueRequest
{
    public required string Value { get; init; }

    // Whether this becomes the list's default. The frontend has a separate Set
    // Default action, so this is only read on add — where the first option of
    // an empty list becomes the default whatever it says.
    public bool IsDefault { get; init; }
}

public sealed record PickListResult
{
    public required SettingsOutcome Outcome { get; init; }

    public PickListView? List { get; init; }

    public PickListRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == SettingsOutcome.Succeeded;

    public static PickListResult Ok(PickListView list) =>
        new() { Outcome = SettingsOutcome.Succeeded, List = list };

    public static PickListResult Failed(SettingsOutcome outcome) =>
        new() { Outcome = outcome };

    public static PickListResult Refused(PickListRefusal refusal) =>
        new() { Outcome = SettingsOutcome.Refused, Refusal = refusal };
}
