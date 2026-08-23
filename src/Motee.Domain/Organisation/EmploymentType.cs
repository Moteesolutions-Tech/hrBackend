namespace Motee.Domain.Organisation;


public enum EmploymentType
{
    FullTime,
    PartTime,
    Temporary,
    Contract,
    Freelance,
    Internship,
    Apprenticeship,
    Casual,
    Seasonal,
    Remote,
    FieldBased,
    
}

public static class EmploymentTypes
{

    public static bool TryParse(string? value, out EmploymentType type)
    {
        type = default;

        if (string.IsNullOrWhiteSpace(value) || int.TryParse(value, out _))
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out type) && Enum.IsDefined(type);
    }
}
