namespace Motee.Domain.Tenants;

// Defaults mirror the wizard's DEFAULT_COMPANY_SETUP, so a tenant that skips a step
// lands on the same values the form would have shown.
public sealed record TenantSettings
{
    public string ManagerTitle { get; init; } = "Line Manager";

    public string DepartmentLabel { get; init; } = "Department";

    public StructureType StructureType { get; init; } = StructureType.Hierarchical;

    // Which parts of the product this company has switched on, as
    // FeatureCatalogue ids. Written by the onboarding wizard's module picker
    // and by the settings panel, and — since the features work went in — read
    // by the permission check, so switching one off actually closes its
    // endpoints. Before that nothing read this at all.
    public IReadOnlyList<string> EnabledModules { get; init; } = [];

    // Enabled but kept out of the sidebar. Presentation, never a gate: a
    // feature can be switched on and tucked away, but one that is switched off
    // is always hidden, or the sidebar offers a link to endpoints that refuse.
    public IReadOnlyList<string> HiddenModules { get; init; } = [];

    // What this company requires of a password, and how long a session lasts.
    //
    // Per tenant rather than per deployment, which is what the security screen
    // has always implied. Identity's options are configured once at start-up for
    // the whole process, so the policy is applied by a validator that reads
    // this instead.
    public Settings.PasswordPolicy PasswordPolicy { get; init; } = new();

    // The company's own clock, as an IANA zone — "Africa/Lagos", "Europe/London".
    //
    // Attendance needs it and nothing else does yet. A punch belongs to a calendar
    // day, and which day that is only has an answer somewhere: at 00:30 in Lagos it is
    // already tomorrow, while UTC still says today. Deciding in UTC would file the
    // first half-hour of every night shift under the wrong date, and nothing about the
    // row would look wrong afterwards.
    //
    // Defaulted from the country at registration rather than left empty, because an
    // absent zone has no safe fallback — UTC is a guess that is wrong for one of the
    // two countries we serve, and silently.
    public string TimeZoneId { get; init; } = "Africa/Lagos";

    // When the company's leave year turns over, as a month and day rather than a date —
    // "1 April" means every April, not one specific one.
    //
    // Not always January. UK companies commonly run April to March to line up with the
    // tax year. Getting it wrong does not fail loudly: it files leave under the wrong
    // year, and the balance is quietly wrong with nothing to point at.
    public int LeaveYearStartMonth { get; init; } = 1;

    public int LeaveYearStartDay { get; init; } = 1;
}
