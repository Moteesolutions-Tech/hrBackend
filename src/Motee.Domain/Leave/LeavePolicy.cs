using Motee.Domain.Common;

namespace Motee.Domain.Leave;

// A kind of leave a company offers.
//
// Tenant-owned rather than an enum, for the same reason access levels are: companies
// offer leave we have not thought of — study leave, sabbaticals, a religious observance
// day — and a fixed list would need a release to accommodate one.
public class LeaveType : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Stable identity for a seeded type, e.g. "annual". Null for tenant-created ones.
    // Kept so a type can still be recognised after it has been renamed, and so the
    // frontend's fixed vocabulary keeps working against renamed types.
    public string? Code { get; set; }

    public required string Name { get; set; }

    // Whether time taken is paid. Drives payroll rather than the balance: unpaid leave
    // still has to be requested and approved, and still shows on the calendar.
    public bool IsPaid { get; set; } = true;

    // Deactivated rather than deleted, because requests point at it and history has to
    // keep reading correctly.
    public bool IsActive { get; set; } = true;

    public int Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// The rules attached to a kind of leave: how much, how much notice, what has to be
// shown, and what happens to what is left at the end of the year.
//
// One policy per leave type per tenant. Eligibility bands — "25 days after five years'
// service" — are a later refinement; a single policy per type is what the screen
// currently configures and what almost every company starts with.
public class LeavePolicy : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid LeaveTypeId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    // The full-year entitlement. Decimal because half days are real and a policy of
    // 22.5 days is not unusual.
    public decimal DaysPerYear { get; set; }

    // How far ahead somebody has to ask. Zero means no notice is required, which is
    // right for sick leave — nobody gives notice of falling ill.
    public int MinNoticeDays { get; set; }

    // Zero means no limit, rather than "no leave allowed". A cap of zero days would make
    // the policy unusable, so it is the natural way to express "unset".
    public int MaxConsecutiveDays { get; set; }

    // Does a bank holiday inside a leave range cost the employee a day? Almost always
    // no — they were not working that day anyway. Modelled as a flag rather than left in
    // the prose note, because the day count depends on it.
    public bool ExcludePublicHolidays { get; set; } = true;

    public bool RequiresMedicalCertificate { get; set; }

    // What has to be attached, in the requester's words: "Fit note for absences over
    // 7 days". Fed to the approval chain's attachment rules.
    public string? AttachmentRequirement { get; set; }

    // Whether this is a bank of days somebody runs down across the year, or a ceiling
    // for one occasion.
    //
    // Annual and sick leave are banks: unused days are genuinely lost at the year end,
    // and "the company is about to lose 340 days" is a number worth putting in front of
    // HR. Maternity and compassionate leave are not — the entitlement is a limit per
    // event, and reporting somebody as losing 365 maternity days would be meaningless
    // and would drown the number that matters.
    //
    // Also decides whether a type takes part in carry-over at all.
    public bool TracksBalance { get; set; } = true;

    public bool CarryOverAllowed { get; set; }

    public decimal MaxCarryOverDays { get; set; }

    // How long carried days survive into the new year before they lapse. Three months is
    // the common answer; zero means they last the whole year.
    public int CarryOverExpiryMonths { get; set; }

    // Whether entitlement builds up through the year or is available in full from day
    // one. Accrual is the honest model for somebody who might leave in March, and the
    // usual one for a first year of service.
    public bool AccruesMonthly { get; set; }

    // Prose the policy screen shows and nothing computes: who qualifies, how public
    // holidays are described to staff, a link to the written policy.
    public string? Eligibility { get; set; }

    public string? PublicHolidayNote { get; set; }

    public string? DocumentUrl { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// A day the company is closed. Tenant-owned and dated, not derived from a library:
// Nigeria and the UK share almost none, both move with announcements, and a company may
// add days of its own.
public class PublicHoliday : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public DateOnly Date { get; set; }

    public required string Name { get; set; }

    // Which jurisdiction it belongs to. A company with staff in both countries observes
    // both sets, and an employee is charged against the one they work in.
    public string? CountryCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
