namespace Motee.Application.Employees;

// The fields a person can supply about themselves. Both flows use this: HR fills it
// in on the manual wizard, the joiner fills the same thing about themselves during
// self-onboarding. One definition rather than two parallel lists that drift.
//
// What is *not* here is the point of it. Job title, department, employment type,
// manager, start date, work location, grade, employee number, status and assigned
// kit are employment decisions. The employer owns them, so a joiner cannot send
// them — not merely cannot see them.
public record SelfProfileRequest
{
    public string? Title { get; init; }

    public string? PreferredName { get; init; }

    public string? MaidenName { get; init; }

    public string? Initials { get; init; }

    public string? Phone { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    public string? Gender { get; init; }

    public string? Nationality { get; init; }

    public string? Ethnicity { get; init; }

    public string? MaritalStatus { get; init; }

    public string? Address { get; init; }

    public string? State { get; init; }

    public string? CountryOfEmployment { get; init; }

    public string? EmergencyContactName { get; init; }

    public string? EmergencyContactRelationship { get; init; }

    public string? EmergencyContactPhone { get; init; }

    public string? EmergencyContactEmail { get; init; }

    // Null means "not part of this request" and leaves what is stored alone; a block
    // with every field null clears it.
    public BankDetailsRequest? BankDetails { get; init; }

    public IdentityDocumentsRequest? IdentityDocuments { get; init; }

    // On the manual form this needs the employee.medical permission. During
    // self-onboarding it does not: the gate exists to stop colleagues reading someone
    // else's health data, not to stop that person entering their own.
    public MedicalRequest? Medical { get; init; }
}

// The wizard's Bank Details, Identity Documents and Medical steps. Sent nested
// inside the create or update request so the whole form commits at once — a person
// half-created because step 3 failed is worse than a rejected form.
//
// Each block is optional. Omitting one leaves whatever is stored untouched; sending
// one with every field null clears it. That distinction matters on update, where
// "I did not open that step" and "I emptied that step" are different intents.

public sealed record BankDetailsRequest
{
    public string? BankName { get; init; }

    public string? AccountNumber { get; init; }

    public string? SortCode { get; init; }

    public string? AccountHolderName { get; init; }
}

public sealed record IdentityDocumentsRequest
{
    public string? NationalIdNumber { get; init; }

    public string? TaxIdNumber { get; init; }

    public string? PensionId { get; init; }

    public string? HousingFundNumber { get; init; }

    public string? DrivingLicenceNumber { get; init; }

    public DateOnly? DrivingLicenceExpiry { get; init; }

    public string? PassportNumber { get; init; }

    public DateOnly? PassportExpiry { get; init; }

    public string? PassportIssuingCountry { get; init; }
}

public sealed record MedicalRequest
{
    public string? Allergies { get; init; }

    public string? Conditions { get; init; }

    public string? Medications { get; init; }

    public string? DietaryRequirements { get; init; }

    public string? AccessibilityNeeds { get; init; }
}

public sealed record BankDetailsDto
{
    public string? BankName { get; init; }

    public string? AccountNumber { get; init; }

    public string? SortCode { get; init; }

    public string? AccountHolderName { get; init; }
}

public sealed record IdentityDocumentsDto
{
    public string? NationalIdNumber { get; init; }

    public string? TaxIdNumber { get; init; }

    public string? PensionId { get; init; }

    public string? HousingFundNumber { get; init; }

    public string? DrivingLicenceNumber { get; init; }

    public DateOnly? DrivingLicenceExpiry { get; init; }

    public string? PassportNumber { get; init; }

    public DateOnly? PassportExpiry { get; init; }

    public string? PassportIssuingCountry { get; init; }
}

public sealed record MedicalDto
{
    public string? Allergies { get; init; }

    public string? Conditions { get; init; }

    public string? Medications { get; init; }

    public string? DietaryRequirements { get; init; }

    public string? AccessibilityNeeds { get; init; }
}
