using Motee.Domain.Authorization;
using Motee.Domain.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Authorization;

// The levels a new tenant starts with. Seven, not eighteen: a company with a screen
// full of levels nobody holds cannot tell which ones matter, and anything niche is
// two minutes' work from a duplicate of the closest one.
//
// These are a starting point, not a fixed set. A tenant renames, edits, deactivates
// and adds to them freely — TemplateSlug survives all of that, which is what keeps
// "reset this to how it shipped" possible.
internal sealed class AccessLevelSeeder(MoteeDbContext dbContext, TimeProvider timeProvider)
{
    // The slug is the enum member camelCased, the same form every enum takes on the
    // wire. Names are the tenant's to change; slugs are not.
    private static readonly (Role Template, string Name, string Description)[] Templates =
    [
        (Role.HrAdmin, "HR Admin",
            "Owns the HR system end to end, including settings and access levels."),

        (Role.HrManager, "HR Manager",
            "Day-to-day people operations. No settings, no access levels."),

        (Role.LineManager, "Line Manager",
            "Approves and sees their own direct reports."),

        (Role.Finance, "Payroll & Finance",
            "Payroll runs, compensation and finance reporting. Not people management."),

        (Role.Recruiter, "Recruiter",
            "The hiring pipeline and headcount, without access to existing staff records."),

        (Role.ReadOnly, "Read-only",
            "Sees everything, changes nothing. What an auditor is usually given."),
    ];

    // Everyone else. Granted nothing beyond the self-service floor, which already
    // reaches a person's own record and their own submissions — so this level exists
    // to be assignable and visible rather than to add anything.
    private const string EmployeeName = "Employee";

    public IReadOnlyList<AccessLevel> Seed(Guid tenantId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        List<AccessLevel> levels =
        [
            .. Templates.Select(template => new AccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                TemplateSlug = Roles.ToSlug(template.Template),
                Name = template.Name,
                Description = template.Description,
                Kind = AccessLevelKind.Default,

                // Active on arrival. A tenant that has to activate seven levels before
                // it can invite anyone would reasonably wonder what it had done wrong.
                Status = AccessLevelStatus.Active,
                Permissions = DefaultAccessLevels.PermissionsFor(template.Template),
                Scope = AccessLevels.ScopeFor(template.Template),
                CreatedAt = now,
                UpdatedAt = now,
            }),

            new AccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                TemplateSlug = "employee",
                Name = EmployeeName,
                Description = "Own record and own submissions. What most of the company holds.",
                Kind = AccessLevelKind.Default,
                Status = AccessLevelStatus.Active,
                Permissions = [],
                Scope = new DataScope { Kind = DataScopeKind.Self },
                CreatedAt = now,
                UpdatedAt = now,
            },
        ];

        dbContext.AccessLevels.AddRange(levels);

        return levels;
    }

    // What the person who registered the tenant gets. Their owner flag is what
    // actually guarantees access; this is so they appear on the Access Levels screen
    // holding something, rather than as an unexplained exception.
    public static AccessLevel FounderLevel(IReadOnlyList<AccessLevel> seeded) =>
        seeded.Single(level => level.TemplateSlug == Roles.ToSlug(Role.HrAdmin));
}
