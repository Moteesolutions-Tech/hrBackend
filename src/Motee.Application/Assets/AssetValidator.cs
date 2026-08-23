using FluentValidation;

namespace Motee.Application.Assets;

public sealed class AssetValidator : AbstractValidator<AssetRequest>
{
    public AssetValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Continue;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(request => request.Tag)
            .NotEmpty().WithMessage("Asset tag is required.")
            .MaximumLength(50);

        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("Asset name is required.")
            .MaximumLength(150);

        RuleFor(request => request.Category).MaximumLength(100);
        RuleFor(request => request.SerialNumber).MaximumLength(100);
        RuleFor(request => request.Notes).MaximumLength(1000);

        // Backdating is normal — kit handed over before anyone recorded it. A date
        // years ahead is a typo.
        RuleFor(request => request.AssignedDate)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1))
            .WithMessage("Assigned date is too far in the future.")
            .When(request => request.AssignedDate.HasValue);
    }
}
