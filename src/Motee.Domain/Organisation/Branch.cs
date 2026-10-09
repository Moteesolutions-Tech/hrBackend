using Motee.Domain.Common;

namespace Motee.Domain.Organisation;

// What kind of site this is. Affects nothing the backend decides — it is how a company
// describes its own estate — but it is a fixed list rather than free text so the filter
// on the branches screen has something stable to group by.
public enum BranchKind
{
    Headquarters,
    Branch,
    RegionalOffice,
    Site,

    // No premises. Kept as a branch rather than as "no branch" so remote staff appear on
    // a headcount by site instead of vanishing from it.
    Remote,
}

public enum BranchStatus
{
    Active,

    // Closed, or not open yet. Retired rather than deleted: people were posted here, and
    // the history has to keep reading.
    Inactive,
}

// A physical site the company operates from.
//
// A different axis from both Department and BusinessUnit, and the distinction is worth
// being precise about because all three look like "a group of people":
//
//   Department   — what you do. Engineering spans every office.
//   BusinessUnit — which part of the group you belong to. A division, a subsidiary.
//   Branch       — where you physically are. Exactly one per person.
//
// A company can have Engineering staff in Lagos and London; neither department nor
// business unit can express "everyone at the Lagos office", which is what a site manager
// needs and what a fire register is.
public class Branch : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    // The tenant's own short form — "LAG", "LDN". Unique within the tenant, because it is
    // what appears on a badge and in a picker where two identical codes cannot be chosen
    // between.
    public required string Code { get; set; }

    public BranchKind Kind { get; set; } = BranchKind.Branch;

    public BranchStatus Status { get; set; } = BranchStatus.Active;

    // Stored as separate lines rather than one blob, because a letter needs them on
    // separate lines and splitting a blob back apart reliably is not possible.
    public IReadOnlyList<string> AddressLines { get; set; } = [];

    public string? City { get; set; }

    public string? Region { get; set; }

    public string? PostalCode { get; set; }

    // The site's own country, which is not always the tenant's. A UK company with a Lagos
    // office is the ordinary case this product exists for, and statutory rules follow the
    // site rather than the headquarters.
    public string? Country { get; set; }

    // IANA, e.g. "Africa/Lagos". Needed before anything schedules across sites — a shift
    // starting at 09:00 means a different instant in each.
    public string? TimeZone { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    // Whoever runs the site. Null is ordinary: a new office often has nobody named yet,
    // and refusing to create one until somebody is would stop a company recording a site
    // it has already opened.
    public Guid? ManagerEmployeeId { get; set; }

    // What the site is planned to grow to, for the headcount screens. Not a limit the
    // backend enforces — a company over target has a planning conversation, not a blocked
    // hire.
    public int? HeadcountTarget { get; set; }

    public DateOnly? OpenedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
