namespace Motee.Api.Http;

// Which browser origins may call this API. Without it a frontend on any other host
// is blocked before the request is even sent, and the failure reads as "the backend
// is down" rather than "the backend did not allow this page".
//
// Nothing here grants access to data — every endpoint still requires its token and
// its permission. CORS only decides which pages a browser will let talk to us.
internal static class CorsSetup
{
    public const string PolicyName = "motee";

    public static IServiceCollection AddMoteeCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string[] origins = configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];

        services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            // No list configured means no browser origin is allowed. Failing closed
            // rather than opening to everything: a forgotten setting should be a
            // visible blocked request, not a silently public API.
            policy.SetIsOriginAllowed(origin => CorsOrigins.IsAllowed(origins, origin));

            policy.AllowAnyHeader();
            policy.AllowAnyMethod();

            // The import template and file downloads arrive as attachments; without
            // this the browser hides the filename from the script that asked for it.
            policy.WithExposedHeaders("Content-Disposition");

            // Deliberately no AllowCredentials. Sessions travel as a bearer token in
            // the Authorization header, never as a cookie, so the browser has no
            // ambient credential to attach — and allowing them would rule out the
            // wildcard matching below.
        }));

        return services;
    }
}

// Kept apart from the wiring so the matching itself can be tested.
internal static class CorsOrigins
{
    public static bool IsAllowed(IReadOnlyList<string> configured, string origin)
    {
        if (configured.Count == 0 || string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        foreach (string allowed in configured)
        {
            if (Matches(allowed.Trim(), origin))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(string allowed, string origin)
    {
        if (allowed.Length == 0)
        {
            return false;
        }

        // "https://*.vercel.app" — every preview deployment of a frontend gets its own
        // hostname, and listing them one by one is not possible. The cost is that any
        // site on that domain may call this API from a browser, so it belongs in a
        // demo environment and not in one holding real records.
        int wildcard = allowed.IndexOf("://*.", StringComparison.OrdinalIgnoreCase);

        if (wildcard < 0)
        {
            // Origins are compared whole. A prefix match would let
            // "https://motee.app.attacker.com" through.
            return string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase);
        }

        string scheme = allowed[..(wildcard + 3)];
        string suffix = allowed[(wildcard + 4)..];

        return origin.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            && origin.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)

            // There has to be something in place of the *, or the bare domain would
            // match its own wildcard entry.
            && origin.Length > scheme.Length + suffix.Length;
    }
}
