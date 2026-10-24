using FluentValidation;

namespace Motee.Application.Employees;

// The invite endpoint had none, and so accepted anything the type system allowed.
//
// It builds an EmployeeRequest internally, but EmployeeValidator runs where a
// controller calls it by hand — and the invite action never did. So the same record
// was refused through POST /employees and written through POST /employees/invite:
// a blank first name, or "notanemail", which then had an invitation queued to it.
//
// Mirrors the rules that apply to what an invitation actually carries. Phone, status
// and the profile blocks are left out because the invite form does not collect them —
// the joiner fills those in themselves, and the record is created Pending with a blank
// phone on purpose.
public sealed class InviteRequestValidator : AbstractValidator<InviteRequest>
{
    public InviteRequestValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Continue;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(request => request.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .MaximumLength(100);

        RuleFor(request => request.MiddleName)
            .MaximumLength(100);

        RuleFor(request => request.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .MaximumLength(100);

        // 256, not the 320 an employee record allows, because an invitation exists to
        // produce a login and the users table stops at 256. Accepting the wider bound
        // here invites somebody to something they cannot finish: the employee saves,
        // the link works, and acceptance fails on the insert — permanently, since the
        // invitation stays valid and they will keep trying.
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Email is required.")
            .MaximumLength(256)
            .EmailAddress().WithMessage("Enter a valid email address.");

        RuleFor(request => request.JobTitle)
            .NotEmpty().WithMessage("Job title is required.")
            .MaximumLength(150);

        RuleFor(request => request.DepartmentId)
            .NotEmpty().WithMessage("Department is required.");

        RuleFor(request => request.EmploymentType)
            .IsInEnum().WithMessage("Unknown employment type.");

        // A joining date decades out is a typo, not a plan.
        RuleFor(request => request.StartDate)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow).AddYears(2))
            .WithMessage("Start date is too far in the future.")
            .When(request => request.StartDate.HasValue);
    }
}
