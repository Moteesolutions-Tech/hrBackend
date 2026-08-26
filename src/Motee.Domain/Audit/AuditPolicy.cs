namespace Motee.Domain.Audit;

// What gets recorded, and what must never be. The whole reason a new module needs no
// audit code is that this decides everything from the entity type alone — so the rules
// live here, in one readable place, rather than as a call each service remembers to add.
public static class AuditPolicy
{
    // Never audited. Not because they are unimportant, but because auditing them is
    // either meaningless or harmful.
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        // Auditing the audit table is a loop.
        nameof(AuditEntry),

        // Machinery, not decisions. A refresh token rotating on every call would bury
        // the entries a person is looking for, and an OTP challenge row says only that
        // Identity is working.
        "RefreshToken",
        "OtpChallengeRecord",

        // Hangfire's own tables, which arrive through the same context.
        "JobQueue",
        "JobParameter",
    };

    // Recorded as "this changed", never "it changed to this".
    //
    // These hold special-category and financial data. An audit row carrying before and
    // after values would reproduce a passport number, an account number or a health
    // note into a table that the medical permission does not gate — so the fact of the
    // change is auditable and the content is not.
    private static readonly HashSet<string> ValuesWithheld = new(StringComparer.Ordinal)
    {
        "EmployeeMedical",
        "EmployeeBankDetails",
        "EmployeeIdentityDocument",
        "EmployeeIdentityDocuments",
    };

    // Bookkeeping, not decisions. Services stamp UpdatedAt on every save, so including
    // these would make every write look like a change — and an entry reading "UpdatedAt
    // changed" and nothing else is exactly the noise that stops people reading a trail.
    //
    // Excluded from the diff rather than merely ignored, so an update where only these
    // moved produces no entry at all.
    private static readonly HashSet<string> Bookkeeping = new(StringComparer.OrdinalIgnoreCase)
    {
        "updatedat",
        "updatedbyuserid",
        "concurrencystamp",
    };

    // Redacted wherever they appear, on any entity. Matched with separators removed, so
    // password_hash and PasswordHash are the same name.
    private static readonly HashSet<string> RedactedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "passwordhash",
        "securitystamp",
        "concurrencystamp",
        "tokenhash",
        "token",
        "secret",
        "apikey",
        "signingkey",
    };

    // Entity type to permission module, so the screen filters by the same vocabulary
    // the access levels use.
    //
    // A type that is not listed falls back to a readable default rather than failing.
    // That is deliberate: a new module is audited correctly the day it ships, and
    // naming it here is a refinement rather than a prerequisite.
    private static readonly Dictionary<string, string> Modules = new(StringComparer.Ordinal)
    {
        ["Employee"] = "organization.employees",
        ["EmployeeInvitation"] = "organization.employees",
        ["EmployeeMedical"] = "employee.medical",
        ["EmployeeBankDetails"] = "organization.employees",
        ["EmployeeIdentityDocument"] = "organization.employees",
        ["Department"] = "organization.departments",
        ["BusinessUnit"] = "organization.structure",
        ["Asset"] = "operations.assets",
        ["AccessLevel"] = "admin.access-levels",
        ["UserAccessLevel"] = "admin.access-levels",
        ["ApplicationUser"] = "admin.access-levels",
        ["Tenant"] = "organization.company",
        ["ExportJob"] = "operations.reports",
        ["StoredFile"] = "operations.documents",
    };

    public static bool IsAudited(string entityType) => !Ignored.Contains(entityType);

    public static bool RecordsValues(string entityType) => !ValuesWithheld.Contains(entityType);

    public static bool IsBookkeeping(string propertyName) =>
        Bookkeeping.Contains(propertyName.Replace("_", string.Empty, StringComparison.Ordinal));

    public static bool IsRedacted(string propertyName) =>
        RedactedProperties.Contains(
            propertyName.Replace("_", string.Empty, StringComparison.Ordinal));

    // "EmployeeInvitation" with no mapping becomes "employee-invitation", which reads
    // acceptably on the screen and is obviously a placeholder to whoever adds the real
    // module name later.
    public static string ModuleFor(string entityType) =>
        Modules.TryGetValue(entityType, out string? module) ? module : Kebab(entityType);

    private static string Kebab(string value)
    {
        System.Text.StringBuilder result = new();

        for (int index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index]))
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(value[index]));
        }

        return result.ToString();
    }
}
