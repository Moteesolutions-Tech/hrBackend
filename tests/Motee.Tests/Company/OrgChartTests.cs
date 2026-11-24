using Motee.Domain.Company;

namespace Motee.Tests.Company;

// The reporting structure behind the organogram.
//
// Two things the screen cannot survive. It finds the root with
// `nodes.find(reportsTo === null)` and renders from it, so a company with two
// people at the top shows one of them and silently drops the other half of the
// company. And `OrgBranch` walks children recursively with no guard, so a
// reporting line that loops is a stack overflow in somebody's browser rather
// than an error anybody can see.
//
// Written before the rules.
public class OrgChartTests
{
    private static readonly Guid Chief = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Head = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Lead = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Junior = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000005");

    private static OrgCharts.Person Person(Guid id, string name, Guid? manager = null) => new()
    {
        Id = id,
        Name = name,
        Initials = name[..1],
        Role = "Engineer",
        Department = "Engineering",
        ManagerId = manager,
    };

    private static List<OrgCharts.Person> Hierarchy() =>
    [
        Person(Chief, "Chief"),
        Person(Head, "Head", Chief),
        Person(Lead, "Lead", Head),
        Person(Junior, "Junior", Lead),
    ];

    // ── the shape ──────────────────────────────────────────────────────────

    [Fact]
    public void EverybodyAppears()
    {
        Assert.Equal(4, OrgCharts.Build(Hierarchy()).Nodes.Count);
    }

    [Fact]
    public void TheTopOfTheChainIsLevelOne()
    {
        OrgChartView chart = OrgCharts.Build(Hierarchy());

        Assert.Equal(1, Node(chart, Chief).Level);
        Assert.Equal(2, Node(chart, Head).Level);
        Assert.Equal(3, Node(chart, Lead).Level);
        Assert.Equal(4, Node(chart, Junior).Level);
    }

    [Fact]
    public void DirectReportsAreCounted()
    {
        OrgChartView chart = OrgCharts.Build(Hierarchy());

        Assert.Equal(1, Node(chart, Chief).DirectReports);
        Assert.Equal(0, Node(chart, Junior).DirectReports);
    }

    [Fact]
    public void SomebodyReportingToNobodyIsARoot()
    {
        Assert.Equal([Chief], OrgCharts.Build(Hierarchy()).Roots);
    }

    // ── more than one top ──────────────────────────────────────────────────

    // Two founders, or a chart somebody has not finished wiring up. The client
    // picks a single root, so the second branch vanishes — which is why the
    // roots come back explicitly.
    [Fact]
    public void TwoPeopleAtTheTopAreBothRoots()
    {
        List<OrgCharts.Person> people = [.. Hierarchy(), Person(Second, "Founder")];

        OrgChartView chart = OrgCharts.Build(people);

        Assert.Equal(2, chart.Roots.Count);
        Assert.Contains(Chief, chart.Roots);
        Assert.Contains(Second, chart.Roots);
    }

    [Fact]
    public void AnEmptyCompanyHasNoRoots()
    {
        OrgChartView chart = OrgCharts.Build([]);

        Assert.Empty(chart.Nodes);
        Assert.Empty(chart.Roots);
    }

    // ── a manager who is not there ─────────────────────────────────────────

    // Somebody whose manager has left. Without the flag the client reads them
    // as a second chief executive.
    [Fact]
    public void SomebodyWhoseManagerIsGoneIsDetachedRatherThanPromoted()
    {
        List<OrgCharts.Person> people = [Person(Head, "Head", Guid.NewGuid())];

        OrgChartView chart = OrgCharts.Build(people);

        OrgChartNode node = Node(chart, Head);

        Assert.True(node.Detached);
        Assert.Null(node.ReportsTo);
        Assert.Contains(Head, chart.Roots);
    }

    [Fact]
    public void SomebodyWithARealManagerIsNotDetached()
    {
        Assert.False(Node(OrgCharts.Build(Hierarchy()), Head).Detached);
    }

    // ── narrowing to the top tiers ─────────────────────────────────────────

    // The leadership view. Narrowing happens after the levels are worked out,
    // so somebody at tier four is dropped rather than promoted to a root.
    [Fact]
    public void NarrowingKeepsTheTopTiers()
    {
        OrgChartView chart = OrgCharts.Build(Hierarchy(), maxLevel: 2);

        Assert.Equal(2, chart.Nodes.Count);
        Assert.Equal([Chief], chart.Roots);
    }

    [Fact]
    public void NarrowingDoesNotPromoteAnybody()
    {
        OrgChartView chart = OrgCharts.Build(Hierarchy(), maxLevel: 3);

        Assert.Equal(3, Node(chart, Lead).Level);
        Assert.Equal(Head, Node(chart, Lead).ReportsTo);
    }

    // A narrowed chart cuts reporting lines to people who were dropped. Those
    // nodes become roots of their own, or the client renders an edge to
    // somebody who is not there.
    [Fact]
    public void ALineToSomebodyDroppedByNarrowingIsCut()
    {
        // Lead's manager Head is at level 2; narrowing to level 3 but removing
        // Head from the set is the same situation a deeper narrow produces.
        List<OrgCharts.Person> people =
        [
            Person(Chief, "Chief"),
            Person(Lead, "Lead", Head),
        ];

        OrgChartView chart = OrgCharts.Build(people);

        OrgChartNode lead = Node(chart, Lead);

        Assert.Null(lead.ReportsTo);
        Assert.True(lead.Detached);
    }

    // ── a line that loops ──────────────────────────────────────────────────

    // Nothing should produce one — the employee service refuses a manager
    // change that would close a cycle — but data loaded straight into the table
    // can, and a loop renders as infinite recursion in the browser.
    [Fact]
    public void ALoopIsLeftOutRatherThanRendered()
    {
        List<OrgCharts.Person> people =
        [
            Person(Head, "Head", Lead),
            Person(Lead, "Lead", Head),
        ];

        OrgChartView chart = OrgCharts.Build(people);

        Assert.Empty(chart.Nodes);
        Assert.Equal(2, chart.Unplaced.Count);
    }

    [Fact]
    public void SomebodyReportingToThemselvesIsLeftOut()
    {
        List<OrgCharts.Person> people = [Person(Head, "Head", Head)];

        OrgChartView chart = OrgCharts.Build(people);

        Assert.Empty(chart.Nodes);
        Assert.Equal([Head], chart.Unplaced);
    }

    // The loop goes; everybody else stays. One bad reporting line must not
    // empty the chart.
    [Fact]
    public void ALoopDoesNotTakeTheRestOfTheCompanyWithIt()
    {
        List<OrgCharts.Person> people =
        [
            .. Hierarchy(),
            Person(Second, "Looper", Second),
        ];

        OrgChartView chart = OrgCharts.Build(people);

        Assert.Equal(4, chart.Nodes.Count);
        Assert.Equal([Second], chart.Unplaced);
    }

    [Fact]
    public void AHealthyChartHasNobodyUnplaced()
    {
        Assert.Empty(OrgCharts.Build(Hierarchy()).Unplaced);
    }

    // ── order ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheNodesComeBackTopTierFirst()
    {
        IReadOnlyList<OrgChartNode> nodes = OrgCharts.Build(Hierarchy()).Nodes;

        Assert.Equal(1, nodes[0].Level);
        Assert.True(nodes[^1].Level >= nodes[0].Level);
    }

    private static OrgChartNode Node(OrgChartView chart, Guid id) =>
        chart.Nodes.Single(node => node.Id == id);
}
