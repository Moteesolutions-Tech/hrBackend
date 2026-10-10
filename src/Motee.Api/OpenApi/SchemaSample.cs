using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Motee.Api.OpenApi;

// Builds a populated example from a schema.
//
// Needed because an explicit example on a media type overrides the one the UI would
// otherwise derive from the schema. The envelope is worth stating by hand — responseCode
// "00" and success true are facts, not placeholders — but writing "data": {} alongside
// them hides exactly the part somebody opened the page to see.
//
// So the envelope is hand-written and the payload is generated, which means the example
// stays complete without anybody maintaining a second copy of every DTO.
internal static class SchemaSample
{
    // Deep enough for an employee carrying an address and a bank block; shallow enough
    // that a self-referencing graph does not unroll into a wall of nesting.
    private const int MaxDepth = 4;

    public static JsonNode? For(IOpenApiSchema? schema, OpenApiDocument? document) =>
        Build(schema, document, depth: 0, seen: []);

    private static JsonNode? Build(
        IOpenApiSchema? schema,
        OpenApiDocument? document,
        int depth,
        HashSet<string> seen)
    {
        if (schema is null || depth > MaxDepth)
        {
            return null;
        }

        // A reference has no shape of its own. Resolving it is what makes the payload
        // appear at all, since every declared DTO arrives as a $ref.
        if (schema is OpenApiSchemaReference reference)
        {
            string id = reference.Reference?.Id ?? string.Empty;

            // A type that contains itself — an employee with a manager, a department with
            // a parent — would otherwise recurse until the depth limit, producing a sample
            // that is mostly its own shadow.
            if (!string.IsNullOrEmpty(id) && !seen.Add(id))
            {
                return null;
            }

            IOpenApiSchema? target = reference.Target
                ?? (document?.Components?.Schemas?.TryGetValue(id, out IOpenApiSchema? found) == true
                    ? found
                    : null);

            return Build(target, document, depth, seen);
        }

        // Listed values beat invented ones: the first permitted value is always valid,
        // where a made-up string for an enum would be a payload the API rejects.
        if (schema.Enum is { Count: > 0 })
        {
            return schema.Enum[0]?.DeepClone();
        }

        JsonSchemaType? type = schema.Type;

        if (type is null)
        {
            // Untyped but with properties is still an object — the generator leaves the
            // type off some composed schemas.
            return schema.Properties is { Count: > 0 }
                ? Properties(schema, document, depth, seen)
                : null;
        }

        if (type.Value.HasFlag(JsonSchemaType.Object))
        {
            return Properties(schema, document, depth, seen);
        }

        if (type.Value.HasFlag(JsonSchemaType.Array))
        {
            JsonNode? item = Build(schema.Items, document, depth + 1, seen);

            // One element, not none. An empty array tells a reader the field exists; it
            // does not tell them what is in it, which is the question.
            return item is null ? new JsonArray() : new JsonArray(item);
        }

        if (type.Value.HasFlag(JsonSchemaType.String))
        {
            return JsonValue.Create(StringFor(schema.Format));
        }

        if (type.Value.HasFlag(JsonSchemaType.Integer))
        {
            return JsonValue.Create(1);
        }

        if (type.Value.HasFlag(JsonSchemaType.Number))
        {
            return JsonValue.Create(1.0m);
        }

        if (type.Value.HasFlag(JsonSchemaType.Boolean))
        {
            return JsonValue.Create(true);
        }

        return null;
    }

    private static JsonObject Properties(
        IOpenApiSchema schema,
        OpenApiDocument? document,
        int depth,
        HashSet<string> seen)
    {
        JsonObject sample = [];

        foreach ((string name, IOpenApiSchema property) in
            schema.Properties ?? new Dictionary<string, IOpenApiSchema>())
        {
            // A fresh branch per property. Sharing the visited set across siblings would
            // let the first address in an object suppress every later one.
            sample[name] = Build(property, document, depth + 1, [.. seen]);
        }

        return sample;
    }

    // Values that look like what the field holds, because a reader checks the shape of a
    // date or an id against their own code. "string" tells them nothing.
    private static string StringFor(string? format) => format switch
    {
        "uuid" => "3fa85f64-5717-4562-b3fc-2c963f66afa6",
        "date" => "2026-01-15",
        "date-time" => "2026-01-15T09:30:00Z",
        "email" => "ada@example.com",
        "uri" => "https://example.com/file.pdf",
        "byte" => "SGVsbG8=",
        _ => "string",
    };
}
