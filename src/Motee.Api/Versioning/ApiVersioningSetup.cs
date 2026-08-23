using Asp.Versioning;

namespace Motee.Api.Versioning;

internal static class ApiVersioningSetup
{
    public const string CurrentVersion = "v1";

    public static IServiceCollection AddMoteeApiVersioning(this IServiceCollection services)
    {
        services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
                options.ApiVersionReader = ApiVersionReader.Combine(
                    new UrlSegmentApiVersionReader(),
                    new HeaderApiVersionReader("X-Api-Version"));
            })
            .AddApiExplorer(options =>
            {
                // Produces group names like "v1", which is what the OpenAPI document
                // name is matched against. wallet-service configures this half but
                // never registers a document per group, so its versions never appear.
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            });

        return services;
    }
}
