using FluentValidation;

namespace Motee.Application.Organisation;

public sealed class DepartmentValidator : AbstractValidator<DepartmentRequest>
{
    public DepartmentValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Continue;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("Department name is required.")
            .MaximumLength(150);

        RuleFor(request => request.Code)
            .NotEmpty().WithMessage("Department code is required.")
            .MaximumLength(20)
            .Matches("^[A-Za-z0-9-]+$")
            .WithMessage("Department code may contain only letters, numbers and hyphens.");

        RuleFor(request => request.Description)
            .MaximumLength(1000);

        RuleFor(request => request.BudgetMonthly)
            .GreaterThanOrEqualTo(0).WithMessage("Budget cannot be negative.")
            .When(request => request.BudgetMonthly.HasValue);

        RuleFor(request => request.Status)
            .IsInEnum().WithMessage("Unknown department status.");
    }
}
