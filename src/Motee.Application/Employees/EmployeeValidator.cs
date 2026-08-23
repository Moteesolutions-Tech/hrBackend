using FluentValidation;

namespace Motee.Application.Employees;

public sealed class EmployeeValidator : AbstractValidator<EmployeeRequest>
{
    public EmployeeValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Continue;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(request => request.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .MaximumLength(100);

        RuleFor(request => request.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .MaximumLength(100);

        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Email is required.")
            .MaximumLength(320)
            .EmailAddress().WithMessage("Enter a valid email address.");

        // The form asks for at least 7 digits.
        RuleFor(request => request.Phone)
            .NotEmpty().WithMessage("Phone number is required.")
            .MinimumLength(7).WithMessage("Enter at least 7 digits.")
            .MaximumLength(50);

        RuleFor(request => request.JobTitle)
            .NotEmpty().WithMessage("Job title is required.")
            .MaximumLength(150);

        RuleFor(request => request.DepartmentId)
            .NotEmpty().WithMessage("Department is required.");

        RuleFor(request => request.EmploymentType)
            .IsInEnum().WithMessage("Unknown employment type.");

        RuleFor(request => request.Status)
            .IsInEnum().WithMessage("Unknown employee status.");

        // A joining date decades out is a typo, not a plan.
        RuleFor(request => request.StartDate)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow).AddYears(2))
            .WithMessage("Start date is too far in the future.")
            .When(request => request.StartDate.HasValue);

        RuleFor(request => request.DateOfBirth)
            .LessThan(_ => DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Date of birth must be in the past.")
            .When(request => request.DateOfBirth.HasValue);

        // Nationality and CountryOfEmployment are free text — length is the only constraint.
        // Neither drives behaviour, so an allowlist would only reject people whose
        // country is spelled differently from ours.
        RuleFor(request => request.Nationality).MaximumLength(100);
        RuleFor(request => request.CountryOfEmployment).MaximumLength(100);

        RuleFor(request => request.EmergencyContactName).MaximumLength(150);
        RuleFor(request => request.EmergencyContactRelationship).MaximumLength(100);
        RuleFor(request => request.EmergencyContactPhone).MaximumLength(50);
        RuleFor(request => request.EmployeeNumber).MaximumLength(50);
        RuleFor(request => request.Address).MaximumLength(500);
        RuleFor(request => request.Initials).MaximumLength(10);

        RuleFor(request => request.EmergencyContactEmail)
            .EmailAddress().WithMessage("Enter a valid emergency contact email.")
            .MaximumLength(320)
            .When(request => !string.IsNullOrWhiteSpace(request.EmergencyContactEmail));

        RuleFor(request => request.BankDetails!)
            .SetValidator(new BankDetailsValidator())
            .When(request => request.BankDetails is not null);

        RuleFor(request => request.IdentityDocuments!)
            .SetValidator(new IdentityDocumentsValidator())
            .When(request => request.IdentityDocuments is not null);

        RuleFor(request => request.Medical!)
            .SetValidator(new MedicalValidator())
            .When(request => request.Medical is not null);
    }
}

// Length only, no formats. The account number is 10 digits in Nigeria and 8 in the
// UK, a sort code exists in one country and not the other, and a tenant may employ
// people outside both. A format rule here would reject correct data.
public sealed class BankDetailsValidator : AbstractValidator<BankDetailsRequest>
{
    public BankDetailsValidator()
    {
        RuleFor(request => request.BankName).MaximumLength(150);
        RuleFor(request => request.AccountNumber).MaximumLength(34);
        RuleFor(request => request.SortCode).MaximumLength(20);
        RuleFor(request => request.AccountHolderName).MaximumLength(150);
    }
}

public sealed class IdentityDocumentsValidator : AbstractValidator<IdentityDocumentsRequest>
{
    public IdentityDocumentsValidator()
    {
        RuleFor(request => request.NationalIdNumber).MaximumLength(50);
        RuleFor(request => request.TaxIdNumber).MaximumLength(50);
        RuleFor(request => request.PensionId).MaximumLength(50);
        RuleFor(request => request.HousingFundNumber).MaximumLength(50);
        RuleFor(request => request.DrivingLicenceNumber).MaximumLength(50);
        RuleFor(request => request.PassportNumber).MaximumLength(50);
        RuleFor(request => request.PassportIssuingCountry).MaximumLength(100);

        // An expiry already past is usually a mistyped year, but it is also how an
        // expired document is recorded — so it is allowed, and only absurd dates are
        // rejected.
        RuleFor(request => request.PassportExpiry)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow).AddYears(30))
            .WithMessage("Passport expiry is too far in the future.")
            .When(request => request.PassportExpiry.HasValue);

        RuleFor(request => request.DrivingLicenceExpiry)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow).AddYears(30))
            .WithMessage("Driving licence expiry is too far in the future.")
            .When(request => request.DrivingLicenceExpiry.HasValue);
    }
}

public sealed class MedicalValidator : AbstractValidator<MedicalRequest>
{
    public MedicalValidator()
    {
        RuleFor(request => request.Allergies).MaximumLength(1000);
        RuleFor(request => request.Conditions).MaximumLength(1000);
        RuleFor(request => request.Medications).MaximumLength(1000);
        RuleFor(request => request.DietaryRequirements).MaximumLength(1000);
        RuleFor(request => request.AccessibilityNeeds).MaximumLength(1000);
    }
}
