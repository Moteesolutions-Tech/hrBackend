using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Motee.Api.OpenApi;

// The generator describes enums by their underlying type, so every one of them
// reaches the frontend as "integer" with an example of 0. The API rejects ordinals
// outright — JsonStringEnumConverter is registered with allowIntegerValues: false —
// so anyone building against the published document gets a 400 from a payload
// Swagger told them to send.
//
// This restates them as the strings they actually are, listing the permitted values
// so the document is the contract rather than an approximation of it.
internal sealed class EnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        Type modelType = context.JsonTypeInfo.Type;
        Type underlyingType = Nullable.GetUnderlyingType(modelType) ?? modelType;

        if (!underlyingType.IsEnum)
        {
            return Task.CompletedTask;
        }

        bool allowsNull = schema.Type?.HasFlag(JsonSchemaType.Null) ?? false;

        schema.Type = allowsNull ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;

        // Cleared because the generator set them for the numeric shape it assumed.
        schema.Format = null;
        schema.Pattern = null;

        // Same policy the serializer applies, so the document lists exactly what the
        // API will accept.
        schema.Enum =
        [
            .. Enum.GetNames(underlyingType)
                .Select(name => (JsonNode)JsonValue.Create(
                    JsonNamingPolicy.CamelCase.ConvertName(name))!),
        ];

        schema.Example = schema.Enum.Count > 0 ? schema.Enum[0].DeepClone() : null;

        return Task.CompletedTask;
    }
}
