using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Motee.Api.Auth;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Api.Jobs;
using Motee.Api.Logging;
using Motee.Api.OpenApi;
using Motee.Api.Tenancy;
using Motee.Api.Versioning;
using Motee.Application;
using Motee.Application.Common;
using Motee.Application.Auth;
using Motee.Application.Localization;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Tenancy;
using Motee.Infrastructure;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddMoteeLogging();

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        // Enums travel as their lowercase name, not an ordinal. Without this a
        // reordered enum would silently change the meaning of stored and sent data.
        // allowIntegerValues: false — an ordinal in a payload is unreadable and
        // silently changes meaning if the enum is ever reordered.
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));

// OpenAPI document generation is the built-in Microsoft.AspNetCore.OpenApi
// generator; Swashbuckle is present only to render the UI over it.
builder.Services.AddMoteeApiVersioning();

// One OpenAPI document per API version. The document name is matched against the
// ApiExplorer group name that versioning produces ("v1").
builder.Services.AddOpenApi(ApiVersioningSetup.CurrentVersion, options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
    options.AddOperationTransformer<AuthorizeSecurityRequirementTransformer>();
    options.AddSchemaTransformer<NumericSchemaTransformer>();
    options.AddSchemaTransformer<EnumSchemaTransformer>();
    options.AddDocumentTransformer<NullableRefSchemaTransformer>();
});

builder.Services.AddExceptionHandler<EnvelopeExceptionHandler>();
builder.Services.AddProblemDetails();


builder.Services.AddMoteeCors(builder.Configuration);
builder.Services.AddMoteeHealth();
builder.Services.AddMoteeRateLimiting(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, RequestContext>();
builder.Services.AddScoped<ICurrentTenant, CurrentTenant>();
builder.Services.AddClientAddressResolution(builder.Configuration);
builder.Services.AddScoped<IVisitorCountryResolver, CdnHeaderCountryResolver>();
builder.Services.AddMoteeAuthentication(builder.Configuration);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMoteeJobs(builder.Configuration);

WebApplication app = builder.Build();

// Configure the HTTP request pipeline.

// Must run before anything reads RemoteIpAddress.
app.UseExceptionHandler();

app.UseForwardedHeaders();
app.UseMiddleware<RequestTracingMiddleware>();
app.UseMoteeRequestLogging();

// Payload logging, off by default outside Production being the wrong default for a
// system holding payroll data: it writes every request and response into CloudWatch,
// which costs per GB and keeps whatever it captured for the retention period.
//
// Sensitive fields are redacted either way, but the safest log is the one that was
// never written, so this is opt-in per environment.
if (builder.Configuration.GetValue<bool>("Logging:RequestBodies"))
{
    app.UseMiddleware<RequestResponseLoggingMiddleware>();
}

// Docs are served outside Production by default. Set "Swagger:Enabled" to true to
// expose them in a deployed environment (staging), or false to force them off.
bool swaggerEnabled = builder.Configuration.GetValue<bool?>("Swagger:Enabled")
    ?? !app.Environment.IsProduction();

if (swaggerEnabled)
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint($"/openapi/{ApiVersioningSetup.CurrentVersion}.json", "Motee API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "Motee API";
    });
}

// Before the redirect and before authentication, for two reasons: a preflight must
// not be answered with a 307 to https, and a 401 or a 500 still has to carry the CORS
// headers — or the browser reports a CORS failure and hides the real status, sending
// whoever is debugging after the wrong problem entirely.
app.UseCors(CorsSetup.PolicyName);

// After CORS so a rejected request still carries the headers the browser needs to read
// it — otherwise a throttled caller sees a CORS error and goes looking for the wrong
// problem. After UseForwardedHeaders, further up, so partitioning uses the real client
// address rather than nginx's.
app.UseRateLimiter();

// Skipped in Development, where the http launch profile has no https port.
//
// Also skipped behind a trusted proxy. There, nginx terminates TLS and already
// redirects http to https, and the container itself is only reachable on loopback - so
// this middleware can never protect anything, and it has two costs: it warns "failed to
// determine the https port" on every single request, and if a port were configured it
// would redirect the container's own health check to a port nothing listens on.
bool behindProxy = builder.Configuration
    .GetSection("Network:TrustedProxies").Get<string[]>() is { Length: > 0 };

if (!app.Environment.IsDevelopment() && !behindProxy)
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.UseMoteeJobsDashboard();

app.MapMoteeHealth();
app.MapControllers();

app.Run();

// Top-level statements compile to an internal Program class. Made visible so tests
// can host the real pipeline — every authorization bug found so far lived in a
// controller, above the layer the service tests reach.
public partial class Program;
