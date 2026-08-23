using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Countries;
using Motee.Application.Assets;
using Motee.Application.Employees;
using Motee.Application.Localization;
using Motee.Application.Organisation;
using Motee.Application.Tenancy;
using Motee.Domain.Countries;

namespace Motee.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ICountryProfile, NigeriaProfile>();
        services.AddSingleton<ICountryProfile, UnitedKingdomProfile>();
        services.AddSingleton<ICountryProfileProvider, CountryProfileProvider>();

        services.AddScoped<ITenantLocaleService, TenantLocaleService>();
        services.AddScoped<IVisitorLocaleService, VisitorLocaleService>();

        // Registered here rather than in the API host: the bulk import validates
        // each row itself, so anything resolving a service needs the validators too.
        services.AddScoped<IValidator<RegisterTenantRequest>, RegisterTenantValidator>();
        services.AddScoped<IValidator<TenantSetupRequest>, TenantSetupValidator>();
        services.AddScoped<IValidator<DepartmentRequest>, DepartmentValidator>();
        services.AddScoped<IValidator<AssetRequest>, AssetValidator>();
        services.AddScoped<IValidator<EmployeeRequest>, EmployeeValidator>();
        services.AddScoped<IValidator<EmployeeImportRow>, EmployeeImportValidator>();

        return services;
    }
}
