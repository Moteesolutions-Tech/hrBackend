using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Motee.Api.Contracts;

namespace Motee.Api.OpenApi;

// Every response this API sends is a ServiceResponse envelope, and almost none of that
// reached the document.
//
// Controllers return IActionResult, which the generator cannot infer a shape from — so
// each operation arrived with no response body at all, and anyone integrating had to read
// the C# to learn that the payload sits under "data" or that failures carry a
// responseCode. The errors were worse: uniform across the whole API, and documented
// nowhere.
//
// This wraps whatever an action declares in the envelope, and attaches the error
// responses it can actually return. Both happen centrally, so an action cannot forget and
// the envelope cannot be described two different ways.
internal sealed class EnvelopeResponsesTransformer : IOpenApiOperationTransformer
{
    private const string Json = "application/json";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Responses ??= [];

        DropImplicitSuccess(operation);
        WrapDeclared(operation, context.Document);
        AddFailures(operation, context);

        return Task.CompletedTask;
    }

    // An action that declares only a 201 still gets an implicit 200 from the framework,
    // which arrives with no schema. Left in, the document offers two success codes where
    // the endpoint has one, and the empty one is the one a reader opens first.
    //
    // Only ever drops the shapeless member of a pair, and only when a sibling has a
    // shape — an endpoint that genuinely returns 200 or 204 keeps both.
    private static void DropImplicitSuccess(OpenApiOperation operation)
    {
        List<KeyValuePair<string, IOpenApiResponse>> successes =
        [
            .. operation.Responses!.Where(entry => entry.Key.StartsWith('2')),
        ];

        if (successes.Count < 2)
        {
            return;
        }

        bool anyWithSchema = successes.Any(entry => HasSchema(entry.Value));

        if (!anyWithSchema)
        {
            return;
        }

        foreach ((string status, IOpenApiResponse response) in successes)
        {
            if (!HasSchema(response))
            {
                operation.Responses!.Remove(status);
            }
        }
    }

    private static bool HasSchema(IOpenApiResponse response) =>
        response is OpenApiResponse concrete
        && concrete.Content?.TryGetValue(Json, out OpenApiMediaType? media) == true
        && media.Schema is not null;

    // The declared payload becomes the envelope's "data". Whatever the generator worked
    // out from ProducesResponseType is kept and nested rather than replaced, so adding
    // that attribute to an action is all anybody has to do.
    //
    // Every declared status, not only the successful ones. An action that documents a 409
    // carrying its payload — "here is the pack, and here is what is still missing" — sends
    // that enveloped like everything else, and documenting the bare payload there would be
    // a shape the API never produces.
    private static void WrapDeclared(OpenApiOperation operation, OpenApiDocument? document)
    {
        foreach ((string status, IOpenApiResponse response) in operation.Responses!.ToList())
        {
            if (response is not OpenApiResponse concrete)
            {
                continue;
            }

            IOpenApiSchema? payload = concrete.Content?.TryGetValue(Json, out OpenApiMediaType? media) == true
                ? media.Schema
                : null;

            bool succeeded = status.StartsWith('2');
            bool created = status == "201";

            concrete.Content = new Dictionary<string, OpenApiMediaType>
            {
                [Json] = new()
                {
                    Schema = Envelope(payload),
                    Example = Example(
                        succeeded
                            ? created ? MoteeStatusCodes.Created : MoteeStatusCodes.Success
                            : status,
                        succeeded,
                        succeeded
                            ? created ? "Created." : "Request successful."
                            : "The message names what went wrong.",

                        // Generated from the declared schema rather than left as {}. An
                        // explicit example overrides the one the UI would derive, so an
                        // empty object here would hide the shape somebody opened the page
                        // to read.
                        data: SchemaSample.For(payload, document)),
                },
            };

            if (string.IsNullOrWhiteSpace(concrete.Description))
            {
                concrete.Description = succeeded
                    ? created ? "Created." : "Success."
                    : "Refused. The payload carries the detail.";
            }
        }
    }

    // Only the failures an operation can actually produce. Listing 401 on an anonymous
    // endpoint, or 403 on one with no permission attribute, teaches people to ignore the
    // list — which is the same as not having one.
    private static void AddFailures(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context)
    {
        IList<object> metadata = context.Description.ActionDescriptor.EndpointMetadata;

        bool anonymous = metadata.OfType<IAllowAnonymous>().Any();
        bool authorised = !anonymous && metadata.OfType<IAuthorizeData>().Any();

        // Anything with a body or a route parameter can be sent something unusable.
        if (operation.RequestBody is not null || operation.Parameters?.Count > 0)
        {
            Add(operation, 400, MoteeStatusCodes.InvalidRequest,
                "The request could not be used. The message names what was wrong with it.");
        }

        if (authorised)
        {
            Add(operation, 401, MoteeStatusCodes.Unauthorized,
                "No token, or one that has expired. Refresh and retry.");

            Add(operation, 403, MoteeStatusCodes.Forbidden,
                "Signed in, but this access level does not grant it.");
        }

        // A route parameter means something is being addressed, and it may not exist.
        if (operation.Parameters?.Any(parameter => parameter.In == ParameterLocation.Path) == true)
        {
            Add(operation, 404, MoteeStatusCodes.NotFound, "No such record.");
        }

        Add(operation, 429, MoteeStatusCodes.TooManyRequests,
            "Rate limited. Back off and retry.");

        Add(operation, 500, MoteeStatusCodes.InternalServerError,
            "Something failed on our side. The message is deliberately vague; the "
            + "correlation id in the response headers identifies the request in our logs.");
    }

    private static void Add(
        OpenApiOperation operation,
        int status,
        string responseCode,
        string description)
    {
        string key = status.ToString();

        // Never overwrite. An action that documented its own 409 with a specific message
        // knows more about it than this does.
        if (operation.Responses!.ContainsKey(key))
        {
            return;
        }

        operation.Responses[key] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [Json] = new()
                {
                    Schema = Envelope(null),
                    Example = Example(responseCode, success: false, description, data: null),
                },
            },
        };
    }

    // The envelope, described once. "data" carries the declared payload, or nothing on a
    // failure — which is why it is nullable rather than required.
    private static OpenApiSchema Envelope(IOpenApiSchema? payload)
    {
        Dictionary<string, IOpenApiSchema> properties = new(StringComparer.Ordinal)
        {
            ["responseCode"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description =
                    "Stable business code, decoupled from the HTTP status. Branch on this "
                    + "rather than on the status: the generic ones mirror HTTP, and the "
                    + "1000 range carries outcomes HTTP has no code for.",
                Enum = [.. MoteeStatusCodes.All.Select(code => (JsonNode)code)],
            },
            ["success"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ["message"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String | JsonSchemaType.Null,
                Description = "Safe to show a user. Present on every failure.",
            },
        };

        properties["data"] = payload ?? new OpenApiSchema
        {
            Type = JsonSchemaType.Null,
            Description = "Null on failure.",
        };

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = properties,
            Required = new HashSet<string>(StringComparer.Ordinal) { "responseCode", "success" },
        };
    }

    private static JsonObject Example(
        string responseCode,
        bool success,
        string message,
        JsonNode? data) =>
        new()
        {
            ["responseCode"] = responseCode,
            ["success"] = success,
            ["message"] = message,
            ["data"] = data,
        };
}
