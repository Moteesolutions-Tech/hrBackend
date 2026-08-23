using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Motee.Infrastructure.Auth;


internal sealed class OtpDebugMode(IConfiguration configuration, IHostEnvironment environment)
{
    public const string FixedCode = "123456";

    public bool Enabled =>
        !environment.IsProduction()
        && bool.TryParse(configuration["App:Debug"], out bool debug)
        && debug;
}
