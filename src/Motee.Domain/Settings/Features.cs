namespace Motee.Domain.Settings;

// A switchable part of the product.
//
// Two screens already claimed to do this and neither did. The onboarding wizard
// writes its module picks to tenants.settings.enabled_modules, and the settings
// panel has an Enabled switch per module — and nothing in the backend has ever
// read either. A company could turn Training off in both places and every
// training endpoint stayed open, which is worse than not offering the switch:
// somebody thinks they withdrew a module and they did not.
//
// The two screens also disagreed about what the modules are called. The wizard
// stores "learning" and "leave-management"; the panel's rows are "mod-training"
// and "mod-leave". So a company that enabled Learning during setup opened
// Settings and saw Training switched off. One catalogue, one set of ids, and
// the wizard's spelling wins because it is the one already in the database.
public sealed record Feature
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Description { get; init; }

    // Whether a tenant that never chose gets it. Mirrors the wizard's
    // EnabledByDefault.
    public bool OnByDefault { get; init; }

    // Cannot be switched off. Not a permission decision — there is no product
    // left without employee records, so offering the switch is offering to
    // break it.
    public bool Essential { get; init; }

    // The permission module keys this feature governs. A key listed here stops
    // answering when the feature is off.
    //
    // Empty on an essential feature, and empty on one whose endpoints do not
    // exist yet. Both are deliberate: the catalogue says what a switch does,
    // and a switch that governs nothing says so rather than appearing to work.
    public IReadOnlyList<string> ModuleKeys { get; init; } = [];
}

public static class FeatureCatalogue
{
    // Ordered as the settings panel lists them, so the screen does not have to
    // hold its own order and drift from this one.
    public static readonly IReadOnlyList<Feature> All =
    [
        new()
        {
            Id = "employee-management",
            Label = "Employee Management",
            Description = "Employee records, departments, documents and self-service.",
            OnByDefault = true,
            Essential = true,
        },
        new()
        {
            Id = "leave-management",
            Label = "Leave & Time-Off",
            Description = "Leave requests, balances, sickness and approvals.",
            OnByDefault = true,
            ModuleKeys = ["time-payroll.leave", "time-payroll.sickness"],
        },
        new()
        {
            Id = "attendance",
            Label = "Attendance",
            Description = "Clock-ins, timesheets and work schedules.",
            ModuleKeys = ["time-payroll.attendance"],
        },

        // Governs nothing yet, and says so rather than appearing to. The
        // frontend has payroll screens; this backend has no pay run, no
        // payslip and no tax calculation, so there is no endpoint for the
        // switch to close. Expenses are deliberately not listed here — a
        // company without payroll still reimburses people.
        new()
        {
            Id = "payroll",
            Label = "Payroll",
            Description = "Pay runs, payslips and compensation.",
        },
        new()
        {
            Id = "benefits",
            Label = "Benefits",
            Description = "Pension, insurance, allowances and staff loans.",
            ModuleKeys = ["organization.benefit-plans", "organization.loans"],
        },
        new()
        {
            Id = "recruitment",
            Label = "Recruitment",
            Description = "Requisitions, candidates and the hiring pipeline.",
            ModuleKeys =
            [
                "talent.workforce-requests",
                "talent.requisition",
                "talent.recruitment",
                "talent.hire-tracker",
            ],
        },
        new()
        {
            Id = "onboarding",
            Label = "Onboarding & Offboarding",
            Description = "New-hire checklists, pre-boarding and leavers.",
            ModuleKeys =
            [
                "talent.onboarding",
                "talent.offboarding",
                "organization.employee-checklist",
            ],
        },
        new()
        {
            Id = "performance",
            Label = "Performance",
            Description = "Reviews, goals and appraisals.",
            ModuleKeys = ["talent.performance"],
        },

        // "learning" is the stored id; the settings panel calls the same thing
        // Training. Aliases below reconcile them.
        new()
        {
            Id = "learning",
            Label = "Training",
            Description = "Courses, quizzes and certifications.",
            ModuleKeys = ["talent.training"],
        },
        new()
        {
            Id = "assets",
            Label = "Assets",
            Description = "Company asset assignment and tracking.",
            ModuleKeys = ["operations.assets"],
        },
        new()
        {
            Id = "helpdesk",
            Label = "Help Desk",
            Description = "Employee support tickets and the knowledge base.",
            ModuleKeys = ["workspace.helpdesk", "workspace.knowledge"],
        },
        new()
        {
            Id = "community",
            Label = "Community",
            Description = "Announcements, kudos, suggestions and surveys.",
            ModuleKeys =
            [
                "workspace.community",
                "workspace.announcements",
                "workspace.kudos",
                "workspace.suggestions",
                "workspace.surveys",
            ],
        },

        // Not on the settings panel, which predates the module. Listed because
        // it is the largest optional thing in the product — 46 endpoints — and
        // a company not running projects should be able to put it away.
        new()
        {
            Id = "projects",
            Label = "Projects",
            Description = "Project plans, allocations, time and go-live.",
            ModuleKeys = ["workspace.projects"],
        },
    ];

    // What the settings panel and the onboarding wizard each called things
    // before there was one catalogue. Read on the way in so stored data and
    // either screen's vocabulary resolve to the same feature.
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["training"] = "learning",
            ["mod-training"] = "learning",
            ["leave"] = "leave-management",
            ["mod-leave"] = "leave-management",
            ["mod-attendance"] = "attendance",
            ["mod-payroll"] = "payroll",
            ["mod-benefits"] = "benefits",
            ["mod-recruitment"] = "recruitment",
            ["mod-onboarding"] = "onboarding",
            ["mod-performance"] = "performance",
            ["mod-assets"] = "assets",
            ["mod-helpdesk"] = "helpdesk",
            ["mod-community"] = "community",
            ["mod-projects"] = "projects",
            ["employees"] = "employee-management",
        };

    public static readonly IReadOnlyList<string> Ids =
        [.. All.Select(feature => feature.Id)];

    public static readonly IReadOnlyList<string> Defaults =
        [.. All.Where(feature => feature.OnByDefault || feature.Essential).Select(feature => feature.Id)];

    private static readonly IReadOnlyDictionary<string, string> GovernedBy = BuildGovernance();

    public static Feature? Find(string id) =>
        Normalise(id) is string resolved
            ? All.FirstOrDefault(feature => string.Equals(feature.Id, resolved, StringComparison.Ordinal))
            : null;

    // The catalogue id behind whatever the caller said, or null if it is not a
    // feature at all. Unknown ids are dropped rather than stored: a typo that
    // round-trips becomes a switch nobody can turn off.
    public static string? Normalise(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        string trimmed = id.Trim();

        if (Ids.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return Ids.First(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        return Aliases.TryGetValue(trimmed, out string? alias) ? alias : null;
    }

    // Which module key belongs to which feature. Built rather than written out,
    // so a key cannot be governed by two features without this throwing on
    // start-up — one key in two places means turning one feature off half-closes
    // the other, and it would only show up in production.
    private static IReadOnlyDictionary<string, string> BuildGovernance()
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);

        foreach (Feature feature in All)
        {
            foreach (string key in feature.ModuleKeys)
            {
                if (map.TryGetValue(key, out string? existing))
                {
                    throw new InvalidOperationException(
                        $"Module key '{key}' is governed by both '{existing}' and '{feature.Id}'.");
                }

                map[key] = feature.Id;
            }
        }

        return map;
    }

    // The feature a module key belongs to, or null where no feature governs it.
    //
    // An ungoverned key is always open. admin.settings is the important one:
    // gating it would let a company switch something off and lose the screen
    // that switches it back on.
    public static string? GovernorOf(string moduleKey) =>
        GovernedBy.TryGetValue(moduleKey, out string? feature) ? feature : null;
}

// Which features a tenant has on, and which of them the sidebar shows.
//
// Sidebar visibility is presentation and never gates anything. It is here
// because the one rule worth enforcing is the pairing: a feature that is off
// cannot be shown, or the sidebar offers a link to endpoints that refuse.
public sealed record FeatureState
{
    public static readonly FeatureState Default = Of(FeatureCatalogue.Defaults, hidden: []);

    public required IReadOnlySet<string> Enabled { get; init; }

    public required IReadOnlySet<string> Hidden { get; init; }

    public static FeatureState Of(
        IEnumerable<string>? enabled,
        IEnumerable<string>? hidden)
    {
        HashSet<string> on = Resolve(enabled);

        // Essentials are forced on rather than validated. A tenant whose stored
        // list predates a feature, or who posted without it, gets the product
        // working rather than an error about a switch they were never shown.
        foreach (Feature feature in FeatureCatalogue.All.Where(candidate => candidate.Essential))
        {
            on.Add(feature.Id);
        }

        HashSet<string> away = Resolve(hidden);

        // Off implies hidden. The frontend already does this when you flick the
        // switch; doing it here as well means a payload that says otherwise —
        // or a feature switched off by a second admin in another tab — still
        // lands consistent.
        foreach (string id in FeatureCatalogue.Ids.Where(id => !on.Contains(id)))
        {
            away.Add(id);
        }

        away.RemoveWhere(id => FeatureCatalogue.All
            .Any(feature => string.Equals(feature.Id, id, StringComparison.Ordinal) && feature.Essential));

        return new FeatureState { Enabled = on, Hidden = away };
    }

    public bool IsEnabled(string featureId) =>
        FeatureCatalogue.Normalise(featureId) is string id && Enabled.Contains(id);

    public bool ShowsInSidebar(string featureId) =>
        FeatureCatalogue.Normalise(featureId) is string id
            && Enabled.Contains(id)
            && !Hidden.Contains(id);

    // Whether this tenant's endpoints under a permission module key should
    // answer at all.
    public bool Allows(string moduleKey) =>
        FeatureCatalogue.GovernorOf(moduleKey) is not string feature || Enabled.Contains(feature);

    private static HashSet<string> Resolve(IEnumerable<string>? ids)
    {
        HashSet<string> resolved = new(StringComparer.Ordinal);

        foreach (string candidate in ids ?? [])
        {
            if (FeatureCatalogue.Normalise(candidate) is string id)
            {
                resolved.Add(id);
            }
        }

        return resolved;
    }
}
