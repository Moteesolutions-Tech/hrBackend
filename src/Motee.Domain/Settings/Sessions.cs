namespace Motee.Domain.Settings;

// What the security screen calls a trusted device.
//
// It is a session. The screen lists devices "verified with your account" and
// says revoking means "the device will need to verify again on next login" —
// which is exactly a live refresh token, and the product already stores those
// with the IP they were issued from. A separate trusted-devices table would be
// a list that revokes nothing: the device would carry on using its refresh
// token, signed in, while the screen showed it gone.
//
// So the list is the user's unexpired, unrevoked refresh tokens, and revoking
// one signs that device out.
public sealed record DeviceName
{
    public required string Label { get; init; }

    public required string Browser { get; init; }

    public required string OperatingSystem { get; init; }
}

public static class UserAgents
{
    // Deliberately short. A full user-agent parser is a dependency that needs
    // updating forever, and the string is only used to help somebody recognise
    // their own laptop in a list of three. Anything unrecognised says so rather
    // than guessing.
    private static readonly (string Token, string Name)[] Browsers =
    [
        // Order matters: Edge and Opera both carry "Chrome", and Chrome carries
        // "Safari". Most specific first.
        ("Edg/", "Edge"),
        ("OPR/", "Opera"),
        ("Firefox/", "Firefox"),
        ("Chrome/", "Chrome"),
        ("Safari/", "Safari"),
    ];

    private static readonly (string Token, string Name)[] Systems =
    [
        ("iPhone", "iOS"),
        ("iPad", "iPadOS"),
        ("Android", "Android"),
        ("Mac OS X", "macOS"),
        ("Macintosh", "macOS"),
        ("Windows", "Windows"),
        ("CrOS", "ChromeOS"),
        ("Linux", "Linux"),
    ];

    private const string Unknown = "Unknown";

    public static DeviceName Describe(string? userAgent)
    {
        string agent = userAgent ?? string.Empty;

        string browser = Match(agent, Browsers);
        string system = Match(agent, Systems);

        // "Chrome on macOS", or "Unknown device" where neither was recognised —
        // which is honest, and is also what a request with no User-Agent header
        // deserves.
        string label = browser == Unknown && system == Unknown
            ? "Unknown device"
            : browser == Unknown
                ? system
                : system == Unknown
                    ? browser
                    : $"{browser} on {system}";

        return new DeviceName
        {
            Label = label,
            Browser = browser,
            OperatingSystem = system,
        };
    }

    private static string Match(string agent, (string Token, string Name)[] table)
    {
        foreach ((string token, string name) in table)
        {
            if (agent.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return Unknown;
    }
}

// What happened on an account, for the Login Activity list.
//
// Read from audit_entries, which already records both a successful sign-in and a
// failed one with the IP, the user agent and the moment. A login-activity table
// would be a second copy of the audit trail that can disagree with it — and the
// auditor's copy is the one that matters.
public enum AccountEventKind
{
    SignIn,
    FailedSignIn,
    SignOut,
    PasswordChanged,
    Other,
}

public static class AccountEvents
{
    // Worked out from what the audit entry says, because the audit trail records
    // the sentence and the status rather than a kind. Keyed off the status
    // first: a 401 against a sign-in is a failure whatever the wording becomes.
    public static AccountEventKind KindOf(string description, int? httpStatus, string action)
    {
        if (string.Equals(action, "Logout", StringComparison.OrdinalIgnoreCase))
        {
            return AccountEventKind.SignOut;
        }

        if (description.Contains("password", StringComparison.OrdinalIgnoreCase)
            && description.Contains("reset", StringComparison.OrdinalIgnoreCase))
        {
            return AccountEventKind.PasswordChanged;
        }

        if (!string.Equals(action, "Login", StringComparison.OrdinalIgnoreCase))
        {
            return AccountEventKind.Other;
        }

        return httpStatus is >= 400 ? AccountEventKind.FailedSignIn : AccountEventKind.SignIn;
    }

    public static bool Succeeded(AccountEventKind kind) => kind != AccountEventKind.FailedSignIn;
}
