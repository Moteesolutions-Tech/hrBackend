namespace Motee.Domain.Tenants;

// How a company describes its own reporting shape. Affects nothing the backend
// enforces — the org chart is whatever the manager links say — but it is what the setup
// wizard asks on its second step, and an option the interface offers that this does not
// accept is a form somebody cannot submit.
public enum StructureType
{
    Hierarchical,

    Flat,

    // Several reporting lines at once: a functional manager and a project one. Listed
    // because the wizard offers it; the backend stores the choice and leaves the actual
    // lines to Employee.ManagerId like the other two.
    Matrix,
}

public static class StructureTypes
{
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
