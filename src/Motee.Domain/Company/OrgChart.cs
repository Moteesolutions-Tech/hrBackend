namespace Motee.Domain.Company;

// Somebody's place in the reporting structure.
//
// A flat list with a parent on each node rather than a nested tree: the client
// already builds the nesting, and a tree in the payload would have to pick one
// root — which is the bug this fixes.
public sealed record OrgChartNode
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Initials { get; init; }

    public string? Role { get; init; }

    public string? Department { get; init; }

    public Guid? ReportsTo { get; init; }

    // How far from the top. 1 for somebody reporting to nobody.
    public required int Level { get; init; }

    public required int DirectReports { get; init; }

    // True where this person reports to somebody who is not in the chart —
    // because they have left, or because the chart was narrowed to the top few
    // tiers. Without it the client treats them as a second chief executive.
    public required bool Detached { get; init; }
}

public sealed record OrgChartView
{
    public required IReadOnlyList<OrgChartNode> Nodes { get; init; }

    // Everybody reporting to nobody. Usually one; more than one is a real state
    // — a company with two founders, or an org chart somebody has not finished
    // wiring up.
    //
    // The client picks a single root and renders from it, so a second person at
    // the top means half the company silently vanishes from the chart. Sent
    // explicitly so the screen can draw them all, or say what it is leaving out.
    public required IReadOnlyList<Guid> Roots { get; init; }

    // Anybody left out because their reporting line loops. Nothing should
    // produce one — the employee service refuses a manager change that would
    // close a cycle — but data loaded straight into the table can, and a loop
    // renders as infinite recursion in the browser rather than as an error.
    public required IReadOnlyList<Guid> Unplaced { get; init; }
}

public static class OrgCharts
{
    public sealed record Person
    {
        public required Guid Id { get; init; }

        public required string Name { get; init; }

        public string? Initials { get; init; }

        public string? Role { get; init; }

        public string? Department { get; init; }

        public Guid? ManagerId { get; init; }
    }

    // Builds the chart, bottom-up from each person's manager.
    //
    // maxLevel narrows it to the top few tiers, which is what the leadership
    // view wants. Narrowing happens after the levels are worked out, so
    // somebody at tier four is dropped rather than promoted to a root.
    public static OrgChartView Build(IReadOnlyList<Person> people, int? maxLevel = null)
    {
        Dictionary<Guid, Person> byId = people.ToDictionary(person => person.Id);

        Dictionary<Guid, int> directReports = people
            .Where(person => person.ManagerId is Guid manager && byId.ContainsKey(manager))
            .GroupBy(person => person.ManagerId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        List<OrgChartNode> nodes = [];
        List<Guid> roots = [];
        List<Guid> unplaced = [];

        foreach (Person person in people)
        {
            // Null, or a manager who is not in this set — somebody who has left,
            // or outside a narrowed chart. Both make this person a top of their
            // own branch, and the second case is flagged so the client does not
            // read them as another chief executive.
            bool detached = person.ManagerId is Guid managerId && !byId.ContainsKey(managerId);

            Guid? reportsTo = detached ? null : person.ManagerId;

            int? level = LevelOf(person, byId);

            if (level is not int depth)
            {
                // Their chain loops. Left out rather than rendered, because the
                // client walks parents recursively and a loop is a stack
                // overflow in somebody's browser.
                unplaced.Add(person.Id);
                continue;
            }

            if (maxLevel is int ceiling && depth > ceiling)
            {
                continue;
            }

            if (reportsTo is null)
            {
                roots.Add(person.Id);
            }

            nodes.Add(new OrgChartNode
            {
                Id = person.Id,
                Name = person.Name,
                Initials = person.Initials,
                Role = person.Role,
                Department = person.Department,
                ReportsTo = reportsTo,
                Level = depth,
                DirectReports = directReports.GetValueOrDefault(person.Id),
                Detached = detached,
            });
        }

        // A narrowed chart cuts reporting lines to people who were dropped.
        // Those nodes become roots of their own, or the client renders an edge
        // to somebody who is not there.
        HashSet<Guid> included = [.. nodes.Select(node => node.Id)];

        for (int index = 0; index < nodes.Count; index++)
        {
            OrgChartNode node = nodes[index];

            if (node.ReportsTo is Guid parent && !included.Contains(parent))
            {
                nodes[index] = node with { ReportsTo = null, Detached = true };
                roots.Add(node.Id);
            }
        }

        return new OrgChartView
        {
            Nodes =
            [
                .. nodes
                    .OrderBy(node => node.Level)
                    .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase),
            ],
            Roots = [.. roots.Distinct()],
            Unplaced = unplaced,
        };
    }

    // How far from the top, or null where the chain loops.
    //
    // Walks upwards with a seen set, the same guard the employee service uses
    // when it refuses a manager change that would close a cycle.
    private static int? LevelOf(Person person, Dictionary<Guid, Person> byId)
    {
        int level = 1;
        HashSet<Guid> seen = [person.Id];
        Person current = person;

        while (current.ManagerId is Guid managerId && byId.TryGetValue(managerId, out Person? manager))
        {
            if (!seen.Add(managerId))
            {
                return null;
            }

            current = manager;
            level++;
        }

        return level;
    }
}
