using Motee.Domain.Common;

namespace Motee.Domain.Employees;

// Bank, identity and medical details are each exactly one per employee, so they
// could have been columns. They are separate tables because they are read far less
// often than the employee row and are more sensitive than it: keeping them out of
// employees means the query behind every list, search and picker cannot carry an
// account number or a passport number with it by accident.

public class EmployeeBankDetails : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public string? BankName { get; set; }

    // Free text rather than a number: Nigerian account numbers are 10 digits, UK are
    // 8 with a separate sort code, and leading zeros matter in both.
    public string? AccountNumber { get; set; }

    // UK only. Nigerian banks have no equivalent, so it stays null there.
    public string? SortCode { get; set; }

    // Often differs from the employee's own name — a joint account, or a maiden name
    // the bank still holds.
    public string? AccountHolderName { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class EmployeeIdentityDocuments : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // Nigerian identifiers. Stored as text: they are identifiers, not quantities, and
    // NIN in particular is 11 digits that must keep any leading zero.
    public string? NationalIdNumber { get; set; }

    public string? TaxIdNumber { get; set; }

    public string? PensionId { get; set; }

    public string? HousingFundNumber { get; set; }

    public string? DrivingLicenceNumber { get; set; }

    public DateOnly? DrivingLicenceExpiry { get; set; }

    public string? PassportNumber { get; set; }

    public DateOnly? PassportExpiry { get; set; }

    // Free text, like Nationality — the passport says what it says.
    public string? PassportIssuingCountry { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// Special-category data under NDPR and GDPR: health information about an identified
// person. It is never returned with the employee record — reading it takes the
// employee.medical permission, which only SuperAdmin and HrAdmin hold.
public class EmployeeMedical : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // Comma-separated as the form collects them. Not split into rows because nothing
    // queries an individual allergy — they are read as a note by a human.
    public string? Allergies { get; set; }

    public string? Conditions { get; set; }

    public string? Medications { get; set; }

    public string? DietaryRequirements { get; set; }

    public string? AccessibilityNeeds { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
