using FluentValidation;
using Motee.Domain.Tenants;

namespace Motee.Application.Tenancy;

public sealed class TenantSetupValidator : AbstractValidator<TenantSetupRequest>
{
    public TenantSetupValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Continue;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(request => request.Industry)
            .Must(TenantSetupCatalogue.Industries.Contains)
            .WithMessage("Choose an industry from the list.");

        RuleFor(request => request.CompanySize)
            .Must(value => CompanySizes.TryParse(value, out _))
            .WithMessage("Choose a company size from the list.");

        RuleFor(request => request.StructureType)
            .Must(value => StructureTypes.TryParse(value, out _))
            .WithMessage("Structure must be hierarchical or flat.");

        RuleFor(request => request.ManagerTitle)
            .NotEmpty().WithMessage("Manager title is required.")
            .MaximumLength(100);

        RuleFor(request => request.DepartmentLabel)
            .NotEmpty().WithMessage("Department label is required.")
            .MaximumLength(100);

        RuleFor(request => request.CompanyPolicies)
            .MaximumLength(20_000);

        // Optional: a tenant may not have a domain of their own, and any provider
        // is acceptable when they do.
        RuleFor(request => request.CompanyEmailDomain)
            .Must(domain => string.IsNullOrWhiteSpace(domain) || CompanyEmailDomain.IsValid(domain))
            .WithMessage("Enter a domain such as acme.com.");

        RuleFor(request => request.EnabledModules)
            .Must(modules => modules.All(TenantSetupCatalogue.Modules.Contains))
            .WithMessage("One or more selected modules do not exist.")
            .Must(modules => modules.Distinct(StringComparer.Ordinal).Count() == modules.Count)
            .WithMessage("The same module was selected more than once.");
    }
}
