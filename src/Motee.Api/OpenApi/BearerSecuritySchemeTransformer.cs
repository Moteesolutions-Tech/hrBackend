using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Motee.Api.OpenApi;

/// <summary>
/// Declares the JWT bearer scheme on the generated OpenAPI document so Swagger UI
/// renders an "Authorize" button and forwards the token on every try-it-out call.
/// </summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeId = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        OpenApiSecurityScheme scheme = new()
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Paste the raw JWT only — Swagger adds the \"Bearer \" prefix.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = scheme;

        return Task.CompletedTask;
    }
}
