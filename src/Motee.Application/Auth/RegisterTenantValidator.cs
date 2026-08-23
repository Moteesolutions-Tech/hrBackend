using FluentValidation;
using Motee.Domain.Common;

namespace Motee.Application.Auth;

public sealed class RegisterTenantValidator : AbstractValidator<RegisterTenantRequest>
{
    public const int MinimumPasswordLength = 8;

    public RegisterTenantValidator()
    {
        // Every field is reported in one response so the form can highlight all of
        // them at once rather than one per submit.
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

        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Work email is required.")
            .MaximumLength(320)
            .Must(BeAWellFormedEmail).WithMessage("Enter a valid email address.");

        RuleFor(request => request.CompanyName)
            .NotEmpty().WithMessage("Company name is required.")
            .MaximumLength(200);

        RuleFor(request => request.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(MinimumPasswordLength)
            .WithMessage($"Password must be at least {MinimumPasswordLength} characters.");

        RuleFor(request => request.CountryCode)
            .NotEmpty().WithMessage("Country is required.")
            .Must(code => CountryCode.TryParse(code, out _))
            .WithMessage("Motee does not operate in that country yet.");
    }

    // FluentValidation's EmailAddress() accepts "user@host" with no dot, which the
    // sign-up form would not.
    private static bool BeAWellFormedEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Any(char.IsWhiteSpace))
        {
            return false;
        }

        string[] parts = email.Split('@');

        return parts.Length == 2
            && parts[0].Length > 0
            && parts[1].Contains('.', StringComparison.Ordinal)
            && !parts[1].StartsWith('.')
            && !parts[1].EndsWith('.');
    }
}
