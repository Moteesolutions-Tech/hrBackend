using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Motee.Api.OpenApi;

/// <summary>
/// Marks only those operations that actually require authentication, so anonymous
/// endpoints (login, health, invite acceptance) are not shown behind a padlock.
/// </summary>
internal sealed class AuthorizeSecurityRequirementTransformer : IOpenApiOperationTransformer
{
    private const string SchemeId = "Bearer";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        IList<object> metadata = context.Description.ActionDescriptor.EndpointMetadata;

        bool allowsAnonymous = metadata.OfType<IAllowAnonymous>().Any();
        bool requiresAuth = metadata.OfType<IAuthorizeData>().Any();

        if (allowsAnonymous || !requiresAuth)
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeId, context.Document)] = [],
        });

        return Task.CompletedTask;
    }
}
