namespace Motee.Domain.Settings;

// What this company requires of a password, and how long a session lasts.
//
// Stored on the tenant and read by the password validator, the sign-in path and
// the token issuer — every one of these does something. The screen had six
// controls and all six were decoration: the backend's requirements were fixed in
// DependencyInjection for every tenant alike, so a company that set a 16
// character minimum with symbols still had eight-character passwords accepted.
public sealed record PasswordPolicy
{
    public static readonly PasswordPolicy Default = new();

    // Floor and ceiling match the screen's slider. Below eight is weaker than
    // the sign-up form has ever promised, and the ceiling exists because a
    // policy nobody can satisfy locks a company out of its own product.
    public const int MinimumLength = 8;

    public const int MaximumLength = 24;

    public const int MinimumSessionMinutes = 15;

    public const int MaximumSessionMinutes = 480;

    public const int MaximumExpiryDays = 365;

    public const int MaximumReuseHistory = 24;

    public int MinLength { get; init; } = MinimumLength;

    public bool RequireUppercase { get; init; }

    public bool RequireNumbers { get; init; }

    // The screen shows one switch for this. The type behind it has both
    // requireSymbols and requireSpecialChars, set to the same value in the demo
    // data and rendered from requireSpecialChars only — two names for one rule,
    // which is how they end up disagreeing. One field here, and the contract
    // accepts either spelling on the way in.
    public bool RequireSymbols { get; init; }

    // How long before a password has to be changed. Zero never expires.
    public int ExpiryDays { get; init; }

    // How many previous passwords cannot be used again. Zero keeps no history.
    public int PreventReuse { get; init; }

    // How long an access token lasts. Per tenant rather than per deployment,
    // which is what the screen has always implied.
    public int SessionTimeoutMinutes { get; init; } = 60;
}

public enum PasswordRefusal
{
    None,
    TooShort,
    NeedsUppercase,
    NeedsNumber,
    NeedsSymbol,
    PreviouslyUsed,
}

public static class PasswordRules
{
    // Checked in the order the screen lists them, so somebody fixing one
    // complaint at a time works down the form rather than around it.
    public static PasswordRefusal Check(PasswordPolicy policy, string? password)
    {
        string candidate = password ?? string.Empty;

        if (candidate.Length < policy.MinLength)
        {
            return PasswordRefusal.TooShort;
        }

        if (policy.RequireUppercase && !candidate.Any(char.IsUpper))
        {
            return PasswordRefusal.NeedsUppercase;
        }

        if (policy.RequireNumbers && !candidate.Any(char.IsDigit))
        {
            return PasswordRefusal.NeedsNumber;
        }

        // Anything that is not a letter or a digit, which is what the screen's
        // "e.g. @, #, !, $" means and wider than a fixed list of punctuation —
        // a policy that rejects £ because it was not on somebody's list is a
        // policy that argues with its own users.
        if (policy.RequireSymbols && !candidate.Any(character => !char.IsLetterOrDigit(character)))
        {
            return PasswordRefusal.NeedsSymbol;
        }

        return PasswordRefusal.None;
    }

    // Brought inside the stated bounds rather than refused. A policy arriving
    // out of range is a slider somebody dragged or an older payload, and
    // rejecting the save leaves the company on whatever they had before with an
    // error they cannot act on.
    public static PasswordPolicy Clamp(PasswordPolicy policy) => policy with
    {
        MinLength = Within(policy.MinLength, PasswordPolicy.MinimumLength, PasswordPolicy.MaximumLength),
        ExpiryDays = Within(policy.ExpiryDays, 0, PasswordPolicy.MaximumExpiryDays),
        PreventReuse = Within(policy.PreventReuse, 0, PasswordPolicy.MaximumReuseHistory),
        SessionTimeoutMinutes = Within(
            policy.SessionTimeoutMinutes,
            PasswordPolicy.MinimumSessionMinutes,
            PasswordPolicy.MaximumSessionMinutes),
    };

    // Whether a password set at this moment has lapsed under the policy.
    //
    // Takes the moment it was set rather than reading a clock, and a null one
    // never expires: an account whose password predates the column has not
    // failed the policy, and locking those people out on the day the policy is
    // first saved would be the whole company at once.
    public static bool HasExpired(
        PasswordPolicy policy,
        DateTimeOffset? changedAt,
        DateTimeOffset now)
    {
        if (policy.ExpiryDays <= 0 || changedAt is not DateTimeOffset set)
        {
            return false;
        }

        return set.AddDays(policy.ExpiryDays) <= now;
    }

    // How many passwords back the policy reaches, including the one in use.
    //
    // The password being used now lives on the account, so the history table
    // holds one fewer than this — a policy of five is four kept rows plus the
    // current one.
    public static int HistoryDepth(PasswordPolicy policy) =>
        Math.Max(0, Math.Min(policy.PreventReuse, PasswordPolicy.MaximumReuseHistory));

    private static int Within(int value, int low, int high) =>
        value < low ? low : value > high ? high : value;
}
