namespace Motee.Domain.Tenants;

// How a company's reporting lines are shaped.
//
// Two of the three are descriptive: a company picks them to say what it is like, and the
// backend stores the choice without enforcing it. Matrix is not — it permits a second
// reporting line that the others do not, which is the one place this setting changes
// what the data may contain.
public enum StructureType
{
    Hierarchical,

    // Everyone reporting to a central point, with few layers between.
    //
    // Descriptive only. Not enforced as a depth limit — a company picks this in its first
    // week to describe itself, and a rule blocking their first team lead would be the
    // product arguing with a label they chose.
    //
    // It briefly defaulted a new starter's manager to the account owner, which stopped
    // being possible when the owner stopped being an employee: there is no central
    // employee to point at, and guessing at one is worse than leaving the blank for
    // whoever the company appoints.
    Flat,

    // Two managers at once: a functional one who owns the person, and a project one who
    // owns the work. The structure the wizard promises in words and draws a diagram of,
    // and the only one that needs somewhere to put the second line.
    Matrix,
}

// What a structure type permits, served rather than inferred.
//
// The wizard needs to know whether to show a second manager field. If that rule lived in
// the client it would be written as `if (structureType === "matrix")` — a copy of a
// backend rule, in a place that cannot be kept in step with it. That is exactly how the
// enum came to offer two values while the interface offered three.
public sealed record StructureTypeCapability
{
    public required StructureType Id { get; init; }

    public required string Label { get; init; }

    // Whether an employee may carry a second, dotted reporting line. True for Matrix
    // alone, and the reason this record exists at all.
    public required bool AllowsSecondaryReporting { get; init; }

}

public static class StructureTypes
{
    // Every structure type and what it permits. The single place the two facts live
    // together, so a new member cannot be added without deciding what it allows.
    public static readonly IReadOnlyList<StructureTypeCapability> All =
    [
        new()
        {
            Id = StructureType.Hierarchical,
            Label = "Hierarchical Structure",
            AllowsSecondaryReporting = false,
        },
        new()
        {
            Id = StructureType.Flat,
            Label = "Flat Structure",
            AllowsSecondaryReporting = false,
        },
        new()
        {
            Id = StructureType.Matrix,
            Label = "Matrix Structure",
            AllowsSecondaryReporting = true,
        },
    ];

    public static StructureTypeCapability Of(StructureType structureType) =>
        All.First(capability => capability.Id == structureType);

    public static bool AllowsSecondaryReporting(StructureType structureType) =>
        Of(structureType).AllowsSecondaryReporting;

    // The permitted values, as they travel on the wire. Derived so a message or a
    // catalogue built from this cannot fall behind the enum.
    public static string Names =>
        string.Join(", ", Enum.GetNames<StructureType>().Select(name => name.ToLowerInvariant()));

    public static bool TryParse(string? value, out StructureType structureType)
    {
        structureType = default;

        return !string.IsNullOrWhiteSpace(value)
            && !int.TryParse(value, out _)
            && Enum.TryParse(value.Trim(), ignoreCase: true, out structureType)
            && Enum.IsDefined(structureType);
    }
}
