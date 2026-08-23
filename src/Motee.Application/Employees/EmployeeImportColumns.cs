using System.Text;

namespace Motee.Application.Employees;

public sealed record ImportColumn
{
    // The CSV header, and the property name on EmployeeImportRow.
    public required string Key { get; init; }

    public required bool Required { get; init; }

    // Shown in the template's example row, so someone filling it in can see the
    // format rather than guess at it.
    public required string Example { get; init; }

    public required string Notes { get; init; }
}

// One definition of the import file, used to build the template that HR downloads.
// The template and the parser drifting apart is exactly how bulk upload ended up
// shipping a file where every row failed.
public static class EmployeeImportColumns
{
    public static readonly IReadOnlyList<ImportColumn> All =
    [
        Column("employeeNumber", false, "EMP-1001", "Your own staff ID. Must be unique."),
        Column("firstName", true, "Chidi", ""),
        Column("middleName", false, "Emeka", ""),
        Column("lastName", true, "Okonkwo", ""),
        Column("email", true, "chidi.okonkwo@example.com", "Must be unique across the company."),
        Column("phone", false, "08012345678", ""),
        Column("jobTitle", true, "Software Engineer", ""),

        // By name, not id. Nobody has a department's UUID to hand when filling in a
        // spreadsheet.
        Column("department", true, "Engineering",
            "Must already exist. Create departments before importing."),

        Column("manager", false, "ada.okafor@example.com",
            "Email, or full name. Managers must appear above their reports in the file."),

        Column("employmentType", true, "fullTime",
            "fullTime, partTime, temporary, contract, freelance, internship, "
            + "apprenticeship, casual, seasonal, remote or fieldBased."),

        Column("startDate", false, "2026-05-01", "yyyy-mm-dd."),
        Column("status", false, "active",
            "pending, onboarded, probation, active, onLeave, offboarding or inactive. "
            + "Defaults to active for imported staff."),

        Column("workLocation", false, "Lagos Head Office", ""),
        Column("workMode", false, "onsite", "remote, hybrid or onsite."),
        Column("grade", false, "L3", ""),

        // One asset per row, which is what the onboarding template collected. More
        // than one goes through the assets module.
        Column("assetTag", false, "AST-0142", "Leave blank if no kit is being issued."),
        Column("assetName", false, "MacBook Pro 14", ""),
        Column("assetCategory", false, "Laptop", ""),
        Column("assetSerialNumber", false, "C02X1234", ""),
        Column("assetAssignedDate", false, "2026-05-01", "yyyy-mm-dd."),
    ];

    // Medical details are deliberately absent. They need the employee.medical
    // permission, which an HR Manager running an import does not hold — and health
    // data does not belong in a spreadsheet emailed between people.

    public static string TemplateCsv()
    {
        StringBuilder csv = new();

        csv.AppendLine(string.Join(",", All.Select(column => column.Key)));
        csv.AppendLine(string.Join(",", All.Select(column => Quote(column.Example))));

        return csv.ToString();
    }

    private static string Quote(string value) =>
        value.Contains(',', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private static ImportColumn Column(string key, bool required, string example, string notes) =>
        new() { Key = key, Required = required, Example = example, Notes = notes };
}
