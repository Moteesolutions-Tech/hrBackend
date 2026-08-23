using FluentValidation;

namespace Motee.Application.Employees;

// Separate from EmployeeValidator because a spreadsheet row is not the manual form.
// Phone is required on the form, which asks for it; it is optional here, because
// migrating five years of staff records should not stall on a missing phone number.
public sealed class EmployeeImportValidator : AbstractValidator<EmployeeImportRow>
{
    public EmployeeImportValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Continue;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(row => row.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .MaximumLength(100);

        RuleFor(row => row.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .MaximumLength(100);

        RuleFor(row => row.Email)
            .NotEmpty().WithMessage("Email is required.")
            .MaximumLength(320)
            .EmailAddress().WithMessage("Enter a valid email address.");

        RuleFor(row => row.JobTitle)
            .NotEmpty().WithMessage("Job title is required.")
            .MaximumLength(150);

        RuleFor(row => row.Department)
            .NotEmpty().WithMessage("Department is required.")
            .MaximumLength(150);

        // Not defaulted. Employment type decides pay, notice and benefits, so a blank
        // column recording a room full of contractors as full-time is a real harm —
        // unlike a missing phone number, which is merely incomplete.
        RuleFor(row => row.EmploymentType)
            .NotNull().WithMessage("Employment type is required.")
            .IsInEnum().WithMessage("Unknown employment type.");

        RuleFor(row => row.Phone).MaximumLength(50);
        RuleFor(row => row.Manager).MaximumLength(320);
        RuleFor(row => row.EmployeeNumber).MaximumLength(50);
        RuleFor(row => row.WorkLocation).MaximumLength(150);
        RuleFor(row => row.Grade).MaximumLength(50);

        RuleFor(row => row.StartDate)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow).AddYears(2))
            .WithMessage("Start date is too far in the future.")
            .When(row => row.StartDate.HasValue);

        // An asset needs both a tag and a name, or there is nothing to identify it by
        // and nothing to call it.
        RuleFor(row => row.AssetName)
            .NotEmpty().WithMessage("An asset tag needs an asset name.")
            .When(row => !string.IsNullOrWhiteSpace(row.AssetTag));

        RuleFor(row => row.AssetTag)
            .NotEmpty().WithMessage("An asset name needs an asset tag.")
            .When(row => !string.IsNullOrWhiteSpace(row.AssetName));

        RuleFor(row => row.AssetTag).MaximumLength(50);
        RuleFor(row => row.AssetName).MaximumLength(150);
        RuleFor(row => row.AssetCategory).MaximumLength(100);
        RuleFor(row => row.AssetSerialNumber).MaximumLength(100);
    }
}
