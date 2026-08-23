namespace Motee.Domain.Tenants;

public enum StructureType
{
    Hierarchical,
    Flat,
}

public static class StructureTypes
{

    public static bool TryParse(string? value, out StructureType structureType)
    {
        structureType = default;

        return !string.IsNullOrWhiteSpace(value)
            && !int.TryParse(value, out _)
            && Enum.TryParse(value.Trim(), ignoreCase: true, out structureType)
            && Enum.IsDefined(structureType);
    }
}
