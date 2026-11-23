namespace Motee.Domain.Projects;

public enum GoLiveCategory
{
    People,
    Data,
    Systems,
    Compliance,
    Support,
}

public sealed record GoLiveItem
{
    // Stable, and what a tick is stored against. Rewording a label must not
    // lose the tick.
    public required string Key { get; init; }

    public required GoLiveCategory Category { get; init; }

    public required string Label { get; init; }
}

public sealed record GoLiveCategoryTally
{
    public required int Done { get; init; }

    public required int Total { get; init; }

    public required int Percent { get; init; }
}

public sealed record GoLiveReadiness
{
    public required int OverallPercent { get; init; }

    public required IReadOnlyDictionary<GoLiveCategory, GoLiveCategoryTally> ByCategory
    {
        get;
        init;
    }
}

// The go-live checklist for an HRIS rollout: a fixed set of checks across five
// areas.
//
// The checks live here rather than being copied into every project. Twenty-five
// rows per project would mean no check could ever be corrected centrally, and
// the first time one was reworded every project in the product would disagree
// about what it said. Only the ticks are stored, against the keys below.
public static class GoLiveTemplate
{
    public static readonly IReadOnlyList<GoLiveItem> All =
    [
        Item("people.super-users", GoLiveCategory.People,
            "Super-users identified per department"),
        Item("people.training", GoLiveCategory.People,
            "Training sessions completed"),
        Item("people.support-roster", GoLiveCategory.People,
            "Support roster confirmed for go-live week"),
        Item("people.comms", GoLiveCategory.People,
            "Change communications sent to all staff"),
        Item("people.manager-sign-off", GoLiveCategory.People,
            "Manager sign-off on team readiness"),

        Item("data.mapping-signed-off", GoLiveCategory.Data,
            "Data migration mapping signed off"),
        Item("data.trial-reconciliation", GoLiveCategory.Data,
            "Trial migration reconciliation passed"),
        Item("data.historical-payroll", GoLiveCategory.Data,
            "Historical payroll data validated"),
        Item("data.cleansing", GoLiveCategory.Data,
            "Data cleansing completed"),
        Item("data.cutover-rehearsed", GoLiveCategory.Data,
            "Final cutover migration rehearsed"),

        Item("systems.payroll-integration", GoLiveCategory.Systems,
            "Payroll integration tested end-to-end"),
        Item("systems.load-testing", GoLiveCategory.Systems,
            "Performance / load testing passed"),
        Item("systems.production-provisioned", GoLiveCategory.Systems,
            "Production environment provisioned"),
        Item("systems.security-review", GoLiveCategory.Systems,
            "Security review passed"),
        Item("systems.monitoring", GoLiveCategory.Systems,
            "Monitoring and alerting configured"),

        Item("compliance.dpia", GoLiveCategory.Compliance,
            "DPIA completed and approved"),
        Item("compliance.statutory-reporting", GoLiveCategory.Compliance,
            "Statutory reporting validated"),
        Item("compliance.audit-trail", GoLiveCategory.Compliance,
            "Audit trail configured"),
        Item("compliance.retention-policy", GoLiveCategory.Compliance,
            "Data retention policy signed off"),
        Item("compliance.regulator-notified", GoLiveCategory.Compliance,
            "Regulator notification (if required) sent"),

        Item("support.helpdesk-scripts", GoLiveCategory.Support,
            "Helpdesk scripts published"),
        Item("support.escalation-path", GoLiveCategory.Support,
            "Escalation path defined"),
        Item("support.hypercare", GoLiveCategory.Support,
            "Hypercare plan approved"),
        Item("support.knowledge-base", GoLiveCategory.Support,
            "Knowledge base articles published"),
        Item("support.post-go-live-review", GoLiveCategory.Support,
            "Post-go-live review scheduled"),
    ];

    // Readiness from the ticks.
    //
    // A tick against a key that is no longer in the template is ignored: a
    // withdrawn or reworded check leaves rows behind, and counting them would
    // report a project as more than ready.
    public static GoLiveReadiness Readiness(IReadOnlyList<string> doneKeys)
    {
        HashSet<string> done = new(doneKeys, StringComparer.Ordinal);

        Dictionary<GoLiveCategory, GoLiveCategoryTally> byCategory = [];

        // Every area is reported, even with nothing ticked: a missing one
        // reads on screen as nought out of nought, which looks like a check
        // that does not exist rather than one nobody has done.
        foreach (GoLiveCategory category in Enum.GetValues<GoLiveCategory>())
        {
            IReadOnlyList<GoLiveItem> items =
                [.. All.Where(item => item.Category == category)];

            int ticked = items.Count(item => done.Contains(item.Key));

            byCategory[category] = new GoLiveCategoryTally
            {
                Done = ticked,
                Total = items.Count,
                Percent = Share(ticked, items.Count),
            };
        }

        int overall = All.Count(item => done.Contains(item.Key));

        return new GoLiveReadiness
        {
            OverallPercent = Share(overall, All.Count),
            ByCategory = byCategory,
        };
    }

    private static int Share(int done, int total) =>
        total == 0
            ? 0
            : (int)Math.Round(done / (decimal)total * 100m, MidpointRounding.AwayFromZero);

    private static GoLiveItem Item(string key, GoLiveCategory category, string label) =>
        new() { Key = key, Category = category, Label = label };
}
