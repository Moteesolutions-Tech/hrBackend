using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Motee.Application.Common;

namespace Motee.Infrastructure.Common;

internal sealed class DebugMode : IDebugMode
{
    // The OTP that verifies when this mode is on. Public because the API surfaces it
    // to developers; it is not a secret, which is the whole problem with it being
    // reachable anywhere real.
    public const string FixedOtpCode = "123456";

#if DEBUG
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
#endif

    // Same signature in both configurations so DI resolves it identically and only the
    // behaviour differs. A primary constructor cannot be used: its parameters are
    // unread in Release, and CS9113 is an error in this build.
    public DebugMode(IConfiguration configuration, IHostEnvironment environment)
    {
#if DEBUG
        _configuration = configuration;
        _environment = environment;
#endif
    }

    // Two gates, deliberately.
    //
    // The runtime one keeps this to Development, so Staging and Production exercise the
    // real flows rather than shortcuts that get switched off later.
    //
    // The compile-time one removes the branch from Release builds entirely. Every
    // published image is built --configuration Release, so no combination of
    // environment variables on a server can reach it. Configuration alone guarding an
    // authentication bypass is one typo away from a breach - and the cost of the extra
    // gate is only that a deployed environment can never have these conveniences, which
    // is the intended answer anyway.
    //
    // Comparing environments rather than matching a string like "prod" matters:
    // IsDevelopment checks the conventional "Development", so this cannot quietly fail
    // open when the value is spelled the standard way.
    public bool Enabled =>
#if DEBUG
        _environment.IsDevelopment()
        && bool.TryParse(_configuration["App:Debug"], out bool debug)
        && debug;
#else
        false;
#endif
}
