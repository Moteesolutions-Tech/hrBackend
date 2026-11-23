using Motee.Domain.Settings;

namespace Motee.Application.Settings;

public enum SettingsOutcome
{
    Succeeded,
    NotFound,
    Refused,
}

public enum SettingsRefusal
{
    None,
    TurnoverNotADate,
    UnknownFeature,
    UnknownTimeZone,
    UnknownLanguage,
}

// The tenant-wide configuration behind the settings screen.
//
// Everything here is read by something. The module switches close endpoints,
// the password policy is what the validator applies, and the holiday year is
// the one the leave balances compute against. That was the whole problem with
// this screen: it had twenty controls and four endpoints, and the controls that
// did have somewhere to save to were saving where nothing read.
public interface ISettingsService
{
    Task<ModuleSettings> ModulesAsync(CancellationToken cancellationToken = default);

    Task<SettingsResult<ModuleSettings>> SaveModulesAsync(
        ModuleSettingsRequest request,
        CancellationToken cancellationToken = default);

    Task<PasswordPolicyView> PasswordPolicyAsync(CancellationToken cancellationToken = default);

    Task<SettingsResult<PasswordPolicyView>> SavePasswordPolicyAsync(
        PasswordPolicyRequest request,
        CancellationToken cancellationToken = default);

    Task<HolidayYearSettings> HolidayYearsAsync(CancellationToken cancellationToken = default);

    Task<SettingsResult<HolidayYearSettings>> SaveHolidayYearAsync(
        HolidayYearRequest request,
        CancellationToken cancellationToken = default);

    Task<SubscriptionView> SubscriptionAsync(CancellationToken cancellationToken = default);
}

public sealed record SettingsResult<T>
{
    public required SettingsOutcome Outcome { get; init; }

    public T? Value { get; init; }

    public SettingsRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == SettingsOutcome.Succeeded;

    public static SettingsResult<T> Ok(T value) =>
        new() { Outcome = SettingsOutcome.Succeeded, Value = value };

    public static SettingsResult<T> Failed(SettingsOutcome outcome) =>
        new() { Outcome = outcome };

    public static SettingsResult<T> Refused(SettingsRefusal refusal) =>
        new() { Outcome = SettingsOutcome.Refused, Refusal = refusal };
}

// ── modules ────────────────────────────────────────────────────────────────

public sealed record ModuleSettings
{
    public required IReadOnlyList<ModuleSettingRow> Modules { get; init; }
}

public sealed record ModuleSettingRow
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required bool Enabled { get; init; }

    public required bool ShowInSidebar { get; init; }

    // Switching it off is not offered. There is no product left without
    // employee records, so the screen shows a locked switch rather than one
    // that fails on save.
    public required bool Essential { get; init; }

    // How many permission modules stop answering when this is switched off.
    // Zero means the switch closes nothing — payroll has screens on the
    // frontend and no endpoints here — and the screen can say so instead of
    // offering a control that appears to work.
    public required int ClosesModules { get; init; }
}

public sealed record ModuleSettingsRequest
{
    public required IReadOnlyList<ModuleChoice> Modules { get; init; }
}

public sealed record ModuleChoice
{
    public required string Id { get; init; }

    public required bool Enabled { get; init; }

    public bool ShowInSidebar { get; init; }
}

// ── password policy ────────────────────────────────────────────────────────

public sealed record PasswordPolicyView
{
    public required PasswordPolicy Policy { get; init; }

    // What the sliders may be set to, sent rather than hard-coded in the client
    // so a bound changed here does not need the screen changed too.
    public int MinLengthFloor => PasswordPolicy.MinimumLength;

    public int MinLengthCeiling => PasswordPolicy.MaximumLength;

    public int SessionFloorMinutes => PasswordPolicy.MinimumSessionMinutes;

    public int SessionCeilingMinutes => PasswordPolicy.MaximumSessionMinutes;
}

public sealed record PasswordPolicyRequest
{
    public int MinLength { get; init; } = PasswordPolicy.MinimumLength;

    public bool RequireUppercase { get; init; }

    public bool RequireNumbers { get; init; }

    // The screen's type carries both names for one rule and renders only
    // requireSpecialChars. Either is accepted so neither spelling silently
    // fails to save.
    public bool RequireSymbols { get; init; }

    public bool? RequireSpecialChars { get; init; }

    public int ExpiryDays { get; init; }

    public int PreventReuse { get; init; }

    public int SessionTimeoutMinutes { get; init; } = 60;

    public bool WantsSymbols => RequireSpecialChars ?? RequireSymbols;
}

// ── holiday year ───────────────────────────────────────────────────────────

public sealed record HolidayYearSettings
{
    // The day the year turns over, as a month and day rather than a date: "1
    // April" means every April, not one specific one.
    public required int StartMonth { get; init; }

    public required int StartDay { get; init; }

    public required IReadOnlyList<HolidayYearCycle> Cycles { get; init; }

    // The screen offers Add Cycle, a label box, two date pickers and Set
    // Active. A cycle is twelve months from the turnover, so all four describe
    // the same one number — and the one somebody typed is the one that would be
    // wrong. Sent so the screen can show the cycles as a consequence of the
    // turnover rather than as a list to edit.
    public bool CyclesAreDerived => true;
}

public sealed record HolidayYearRequest
{
    public required int StartMonth { get; init; }

    public required int StartDay { get; init; }
}

// ── subscription ───────────────────────────────────────────────────────────

public sealed record SubscriptionView
{
    public required string CompanyName { get; init; }

    public required string Plan { get; init; }

    public required string PlanLabel { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? TrialEndsAt { get; init; }

    public string? BillingEmail { get; init; }

    // Counted from the accounts that exist. The screen's demo data carries a
    // stored usedSeats, which is a number that disagrees with the user list the
    // moment somebody is deactivated.
    public required int UsedSeats { get; init; }

    // Null, and null is the honest answer: nothing in this product stores a
    // seat allowance. The screen draws "64 of 100" from demo data, and a made-up
    // ceiling is worse than no ceiling — somebody plans a hiring round against
    // it.
    public int? SeatLimit { get; init; }

    // False, because there is no payment provider, no invoice table and nothing
    // that could produce a line of billing history. Sent so the screen can say
    // where invoices come from instead of rendering an empty table that reads
    // as a fault.
    public bool InvoicesAvailable => false;
}
