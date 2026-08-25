using FluentValidation;

namespace Motee.Application.Organisation;

public sealed class BusinessUnitValidator : AbstractValidator<BusinessUnitRequest>
{
    public BusinessUnitValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Continue;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("Business unit name is required.")
            .MaximumLength(150);

        // Optional, unlike a department code: not every company codes its divisions,
        // and requiring one would make them invent placeholders.
        RuleFor(request => request.Code)
            .MaximumLength(50)
            .Matches("^[A-Za-z0-9-]+$")
            .WithMessage("Business unit code may contain only letters, numbers and hyphens.")
            .When(request => !string.IsNullOrWhiteSpace(request.Code));

        RuleFor(request => request.Description)
            .MaximumLength(500);
    }
}
