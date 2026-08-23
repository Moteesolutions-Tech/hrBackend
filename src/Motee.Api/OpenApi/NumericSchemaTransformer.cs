using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Motee.Api.OpenApi;


internal sealed class NumericSchemaTransformer : IOpenApiSchemaTransformer
{
    private static readonly Dictionary<Type, JsonSchemaType> NumericTypes = new()
    {
        [typeof(decimal)] = JsonSchemaType.Number,
        [typeof(double)] = JsonSchemaType.Number,
        [typeof(float)] = JsonSchemaType.Number,
        [typeof(int)] = JsonSchemaType.Integer,
        [typeof(long)] = JsonSchemaType.Integer,
        [typeof(short)] = JsonSchemaType.Integer,
        [typeof(sbyte)] = JsonSchemaType.Integer,
        [typeof(byte)] = JsonSchemaType.Integer,
        [typeof(uint)] = JsonSchemaType.Integer,
        [typeof(ulong)] = JsonSchemaType.Integer,
        [typeof(ushort)] = JsonSchemaType.Integer,
    };

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        Type modelType = context.JsonTypeInfo.Type;
        Type underlyingType = Nullable.GetUnderlyingType(modelType) ?? modelType;

        if (!NumericTypes.TryGetValue(underlyingType, out JsonSchemaType numericType))
        {
            return Task.CompletedTask;
        }

        bool allowsNull = schema.Type?.HasFlag(JsonSchemaType.Null) ?? false;

        schema.Type = allowsNull ? numericType | JsonSchemaType.Null : numericType;
        schema.Pattern = null;

        return Task.CompletedTask;
    }
}
