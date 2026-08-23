using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Motee.Api.OpenApi;

// A nullable object property is emitted as oneOf: [null, TheType]. Swagger UI renders
// the first branch it finds, so every optional block — bankDetails, identityDocuments,
// medical — shows as a bare null and the reader cannot see the shape without hunting
// through the schema list.
//
// Collapsing it to the reference alone makes the example expand. Optionality is not
// lost: the property stays out of the required list, which is what actually says it
// may be omitted.
//
// A document transformer rather than a schema one, because the oneOf wrapper is
// created when a schema is referenced, which is after per-schema transformers run.
internal sealed class NullableRefSchemaTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (document.Components?.Schemas is null)
        {
            return Task.CompletedTask;
        }

        foreach (IOpenApiSchema schema in document.Components.Schemas.Values)
        {
            if (schema.Properties is null)
            {
                continue;
            }

            foreach (IOpenApiSchema property in schema.Properties.Values)
            {
                Collapse(property);
            }
        }

        return Task.CompletedTask;
    }

    private static void Collapse(IOpenApiSchema property)
    {
        if (property is not OpenApiSchema concrete || concrete.OneOf is not { Count: 2 } branches)
        {
            return;
        }

        // Exactly the shape the generator produces for "this reference may be null".
        // Anything else is a real union and is left alone.
        IOpenApiSchema? reference = branches.FirstOrDefault(branch => branch is OpenApiSchemaReference);

        bool nullable = branches.Any(branch =>
            branch is OpenApiSchema { Type: JsonSchemaType.Null });

        if (reference is null || !nullable)
        {
            return;
        }

        concrete.OneOf = null;
        concrete.AllOf = [reference];
    }
}
