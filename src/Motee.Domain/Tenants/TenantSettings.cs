namespace Motee.Domain.Tenants;

// Defaults mirror the wizard's DEFAULT_COMPANY_SETUP, so a tenant that skips a step
// lands on the same values the form would have shown.
public sealed record TenantSettings
{
    public string ManagerTitle { get; init; } = "Line Manager";

    public string DepartmentLabel { get; init; } = "Department";

    public StructureType StructureType { get; init; } = StructureType.Hierarchical;

    public IReadOnlyList<string> EnabledModules { get; init; } = [];

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
