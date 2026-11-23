using Motee.Domain.Projects;

namespace Motee.Tests.Projects;

// Go-live readiness: a fixed set of checks across five areas, rolling up into
// one score.
//
// The checks live in code and only the ticks are stored. A fixed template
// copied into every project would be twenty-five rows per project that nobody
// can ever correct centrally, and the first time a check is reworded every
// project in the product disagrees about what it was.
//
// Written before the template.
public class GoLiveReadinessTests
{
    [Fact]
    public void TheTemplateCoversEveryArea()
    {
        Assert.Equal(
            Enum.GetValues<GoLiveCategory>(),
            GoLiveTemplate.All.Select(item => item.Category).Distinct().Order());
    }

    [Fact]
    public void EveryCheckHasItsOwnKey()
    {
        IReadOnlyList<string> keys = [.. GoLiveTemplate.All.Select(item => item.Key)];

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryCheckSaysWhatItIs()
    {
        Assert.All(
            GoLiveTemplate.All,
            item => Assert.False(string.IsNullOrWhiteSpace(item.Label)));
    }

    [Fact]
    public void NothingTickedIsNoReadiness()
    {
        GoLiveReadiness readiness = GoLiveTemplate.Readiness([]);

        Assert.Equal(0, readiness.OverallPercent);
        Assert.All(
            readiness.ByCategory.Values,
            tally =>
            {
                Assert.Equal(0, tally.Done);
                Assert.Equal(0, tally.Percent);
            });
    }

    [Fact]
    public void EveryAreaIsReportedEvenBeforeAnythingIsTicked()
    {
        // A missing area reads on screen as nought out of nought, which looks
        // like a check that does not exist rather than one nobody has done.
        Assert.Equal(
            Enum.GetValues<GoLiveCategory>().Length,
            GoLiveTemplate.Readiness([]).ByCategory.Count);
    }

    [Fact]
    public void EverythingTickedIsReady()
    {
        Assert.Equal(
            100,
            GoLiveTemplate.Readiness(
                [.. GoLiveTemplate.All.Select(item => item.Key)]).OverallPercent);
    }

    [Fact]
    public void ReadinessIsReportedAreaByArea()
    {
        IReadOnlyList<GoLiveItem> people =
            [.. GoLiveTemplate.All.Where(item => item.Category == GoLiveCategory.People)];

        GoLiveReadiness readiness = GoLiveTemplate.Readiness(
            [.. people.Take(people.Count - 1).Select(item => item.Key)]);

        GoLiveCategoryTally tally = readiness.ByCategory[GoLiveCategory.People];

        Assert.Equal(people.Count - 1, tally.Done);
        Assert.Equal(people.Count, tally.Total);
        Assert.Equal(0, readiness.ByCategory[GoLiveCategory.Data].Done);
    }

    // A check that was reworded or withdrawn leaves ticks behind pointing at a
    // key that is no longer in the template. Counting them would report a
    // project as more than ready.
    [Fact]
    public void ATickAgainstACheckThatNoLongerExistsIsIgnored()
    {
        Assert.Equal(0, GoLiveTemplate.Readiness(["people.no-such-check"]).OverallPercent);
    }

    [Fact]
    public void ReadinessNeverExceedsReady()
    {
        IReadOnlyList<string> everything =
        [
            .. GoLiveTemplate.All.Select(item => item.Key),
            "systems.withdrawn-check",
        ];

        Assert.Equal(100, GoLiveTemplate.Readiness(everything).OverallPercent);
    }
}
