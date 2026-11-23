using Motee.Domain.Common;

namespace Motee.Domain.Settings;

// One configurable option on a dropdown.
//
// Only for fields that are free text in the database. The settings panel offers
// pick lists for Department, Job Title and Location, and all three are already
// tables — departments, positions and branches. Storing them here as well would
// give the product two lists of departments that disagree, and adding one in
// Settings would not create a department, so the employee form would not offer
// it. Those three are served from their own tables and refuse writes here,
// naming the screen that does own them.
public class PickListOption : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Which list it belongs to, as a PickListCatalogue key.
    public required string ListKey { get; set; }

    public required string Value { get; set; }

    // The one the form starts on. At most one per list, and the list has one as
    // long as it has any options — see PickListRules.
    public bool IsDefault { get; set; }

    public int Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// Where a list's values actually come from.
public enum PickListSource
{
    // Stored as pick_list_options rows and editable here.
    Configured,

    // Already a table of its own. Read-only here.
    Entity,
}

public sealed record PickListDefinition
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required PickListSource Source { get; init; }

    // For an entity-backed list: the screen that owns it. Sent to the client so
    // it can point somebody at the right place instead of showing a dialog that
    // saves into a second list.
    public string? OwnedBy { get; init; }

    public string? OwnerRoute { get; init; }
}

public static class PickListCatalogue
{
    public static readonly IReadOnlyList<PickListDefinition> All =
    [
        new()
        {
            Key = "department",
            Name = "Department",
            Description = "Departments available across employee records.",
            Source = PickListSource.Entity,
            OwnedBy = "Departments",
            OwnerRoute = "/organization/departments",
        },
        new()
        {
            Key = "job-title",
            Name = "Job Title",
            Description = "Standard job titles for new hires and roles.",
            Source = PickListSource.Entity,
            OwnedBy = "Roles",
            OwnerRoute = "/organization/roles",
        },
        new()
        {
            Key = "location",
            Name = "Location",
            Description = "Office locations and remote regions.",
            Source = PickListSource.Entity,
            OwnedBy = "Branches",
            OwnerRoute = "/organization/branches",
        },

        // Genuinely free text in the database, which is what a pick list is for:
        // without one, "Laptop", "laptops" and "Lap top" are three categories
        // and the asset register cannot be grouped.
        new()
        {
            Key = "asset-category",
            Name = "Asset Category",
            Description = "Categories for the asset register.",
            Source = PickListSource.Configured,
        },
        new()
        {
            Key = "certification-category",
            Name = "Certification Category",
            Description = "Categories for recorded professional credentials.",
            Source = PickListSource.Configured,
        },
    ];

    public static PickListDefinition? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(list =>
                string.Equals(list.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}

public enum PickListRefusal
{
    None,
    ValueRequired,
    ValueTooLong,
    DuplicateValue,
    ListNotConfigurable,
    LastOption,
}

public static class PickListRules
{
    public const int MaximumValueLength = 120;

    public static PickListRefusal CheckValue(
        PickListDefinition list,
        string? value,
        IReadOnlyList<PickListOption> existing,
        Guid? editing = null)
    {
        if (list.Source != PickListSource.Configured)
        {
            return PickListRefusal.ListNotConfigurable;
        }

        string trimmed = (value ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return PickListRefusal.ValueRequired;
        }

        if (trimmed.Length > MaximumValueLength)
        {
            return PickListRefusal.ValueTooLong;
        }

        // Case-insensitively, which is the comparison the dialog uses. A list
        // holding both "Laptop" and "laptop" is a list somebody has to look at
        // twice to use once.
        bool duplicate = existing.Any(option =>
            option.Id != editing
                && string.Equals(option.Value, trimmed, StringComparison.OrdinalIgnoreCase));

        return duplicate ? PickListRefusal.DuplicateValue : PickListRefusal.None;
    }

    // Exactly one default, and the list has one as long as it has anything.
    //
    // Applied after every change rather than at the point of each one. Deleting
    // the default promotes the next option, adding to an empty list makes that
    // option the default, and a payload that marks two defaults lands with one —
    // which the frontend gets right on delete and wrong nowhere else only
    // because it never persists.
    public static void Settle(IReadOnlyList<PickListOption> options)
    {
        if (options.Count == 0)
        {
            return;
        }

        PickListOption? chosen = options.FirstOrDefault(option => option.IsDefault);

        chosen ??= options[0];

        foreach (PickListOption option in options)
        {
            option.IsDefault = ReferenceEquals(option, chosen);
        }
    }

    // Next position at the end of the list, 1-based so an unsequenced row
    // sorts before everything rather than into the middle.
    public static int NextSequence(IReadOnlyList<PickListOption> options) =>
        options.Count == 0 ? 1 : options.Max(option => option.Sequence) + 1;
}
