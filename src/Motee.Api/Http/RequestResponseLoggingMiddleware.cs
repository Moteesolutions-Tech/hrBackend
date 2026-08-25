using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Motee.Api.Http;

// Logs what came in and what went back out, for every request. The per-request line
// from Serilog gives method, path, status and duration; this is what it does not give -
// the payload - which is the difference between "a 400 happened" and knowing why.
//
// Four things this does that a naive version does not, each of which is a real failure
// waiting to happen:
//
//   1. Sensitive fields are replaced. /auth/register carries a plaintext password and
//      /auth/verify-otp a live code. CloudWatch keeps them for the retention period and
//      every principal with logs:GetLogEvents can read them, so writing one is
//      disclosing it.
//   2. Bodies over a cap are summarised, not logged. A bulk import or a CSV export
//      would otherwise be buffered whole into memory and then billed for by the GB.
//   3. Only textual content types are captured. Reading a PDF or an image as UTF-8
//      produces noise and wastes the same memory.
//   4. The response stream is restored in a finally. Miss that and every response after
//      an exception is truncated - an outage caused entirely by the logging.
internal sealed class RequestResponseLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestResponseLoggingMiddleware> logger)
{
    private const int MaxBytes = 32 * 1024;

    // Compared with underscores and hyphens removed, so join_token and joinToken both
    // match the same entry.
    private static readonly string[] Sensitive =
    [
        "password", "currentpassword", "newpassword", "confirmpassword",
        "code", "otp", "token", "jointoken", "refreshtoken", "accesstoken",
        "secret", "apikey", "signingkey", "passwordhash",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        // The same paths the request log already drops. The jobs dashboard polls every
        // two seconds and would otherwise dominate this too.
        if (IsRoutine(context.Request.Path))
        {
            await next(context);
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        string requestBody = await ReadRequestAsync(context.Request);

        Stream originalBody = context.Response.Body;
        using MemoryStream buffer = new();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();

            string responseBody = ReadResponse(context.Response, buffer);

            // Restored and flushed before anything else can throw, so a logging failure
            // can never cost the caller their response.
            context.Response.Body = originalBody;
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);

            logger.LogInformation(
                "API {Method} {Path} -> {Status} in {Elapsed} ms\nrequest: {Request}\nresponse: {Response}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                Redact(requestBody),
                Redact(responseBody));
        }
    }

    private static bool IsRoutine(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/jobs", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextual(string? contentType) =>
        contentType is not null
        && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("text/", StringComparison.OrdinalIgnoreCase));

    private static async Task<string> ReadRequestAsync(HttpRequest request)
    {
        if (request.ContentLength is not > 0)
        {
            return string.Empty;
        }

        if (!IsTextual(request.ContentType))
        {
            return $"<{request.ContentType}, {request.ContentLength} bytes>";
        }

        if (request.ContentLength > MaxBytes)
        {
            return $"<{request.ContentLength} bytes, too large to log>";
        }

        // Without this the body is forward-only: the model binder would find nothing
        // left to read.
        request.EnableBuffering();

        using StreamReader reader = new(
            request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        string body = await reader.ReadToEndAsync();

        request.Body.Position = 0;

        return body;
    }

    private static string ReadResponse(HttpResponse response, MemoryStream buffer)
    {
        if (buffer.Length == 0)
        {
            return string.Empty;
        }

        if (!IsTextual(response.ContentType))
        {
            return $"<{response.ContentType}, {buffer.Length} bytes>";
        }

        if (buffer.Length > MaxBytes)
        {
            return $"<{buffer.Length} bytes, too large to log>";
        }

        buffer.Position = 0;

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Parsed rather than pattern-matched. A regex over raw text misses a field split
    // across lines and mangles any value containing a quote.
    //
    // Internal so it can be tested directly: this is the one part where a mistake means
    // a credential in a log that many people can read, and it should not be reachable
    // only through a full HTTP round trip.
    internal static string Redact(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.StartsWith('<'))
        {
            return body;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            using MemoryStream output = new();

            using (Utf8JsonWriter writer = new(output))
            {
                WriteRedacted(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (JsonException)
        {
            // Malformed JSON is often exactly why a request failed, so it is worth
            // knowing - but it cannot be parsed to redact, and may still hold a
            // password. Reporting the shape is the safe half of the answer.
            return $"<unparseable, {body.Length} chars>";
        }
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (IsSensitive(property.Name))
                    {
                        writer.WriteString(property.Name, "[redacted]");
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteRedacted(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteRedacted(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitive(string name) =>
        Sensitive.Contains(
            name.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal),
            StringComparer.OrdinalIgnoreCase);
}
