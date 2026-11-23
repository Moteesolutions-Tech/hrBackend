using Motee.Domain.Notifications;
using Motee.Domain.Settings;

namespace Motee.Tests.Settings;

// Which notifications reach somebody, and on which channel.
//
// NotificationKind's own comment says the kind is "what preferences,
// deduplication and 'stop telling me about this' all hang off". Deduplication
// was built; preferences were not, so every switch on the panel was decoration
// and the bell told everybody everything.
//
// Written before the rules.
public class NotificationPreferenceTests
{
    // ── the groups hold together ───────────────────────────────────────────

    // A kind in no group is a notification nobody can turn off.
    [Fact]
    public void EveryKindOfNotificationBelongsToAGroup()
    {
        Assert.All(
            Enum.GetValues<NotificationKind>(),
            kind => Assert.False(string.IsNullOrWhiteSpace(NotificationGroups.GroupOf(kind))));
    }

    [Fact]
    public void NoKindIsInTwoGroups()
    {
        List<NotificationKind> all = [.. NotificationGroups.All.SelectMany(group => group.Kinds)];

        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void EveryGroupNamesAtLeastOneKind()
    {
        Assert.All(NotificationGroups.All, group => Assert.NotEmpty(group.Kinds));
    }

    [Fact]
    public void AGroupThatIsNotInTheCatalogueIsNotAGroup()
    {
        Assert.Null(NotificationGroups.Find("payroll"));
        Assert.Null(NotificationGroups.Find(null));
    }

    // ── only channels that deliver ─────────────────────────────────────────

    [Fact]
    public void TheBellDelivers()
    {
        Assert.True(NotificationPreferenceRules.Delivers(NotificationChannel.InApp));
    }

    // There is no device registration, no push certificate and nothing to send
    // to. A stored true here is a true that means nothing, and the next person
    // to read the row believes somebody is being notified.
    [Fact]
    public void PushDoesNotDeliverSoItCannotBeSwitchedOn()
    {
        ChannelChoice asked = new() { InApp = true, Push = true };

        Assert.False(NotificationPreferenceRules.Possible(asked).Push);
    }

    [Fact]
    public void EmailDoesNotCarryNotificationsYetSoItCannotBeSwitchedOn()
    {
        ChannelChoice asked = new() { InApp = true, Email = true };

        Assert.False(NotificationPreferenceRules.Possible(asked).Email);
    }

    [Fact]
    public void TheChannelsReportWhyTheyAreNotOffered()
    {
        Assert.Equal(
            ChannelAvailability.Available, NotificationGroups.Channels[NotificationChannel.InApp]);
        Assert.Equal(
            ChannelAvailability.NotWired, NotificationGroups.Channels[NotificationChannel.Email]);
        Assert.Equal(
            ChannelAvailability.Unavailable, NotificationGroups.Channels[NotificationChannel.Push]);
    }

    // ── what reaches the bell ──────────────────────────────────────────────

    // Somebody who has never opened the panel still has to hear that their
    // contract is lapsing.
    [Fact]
    public void SomebodyWithNoSavedPreferencesStillHearsEverything()
    {
        Assert.All(
            Enum.GetValues<NotificationKind>(),
            kind => Assert.True(NotificationPreferenceRules.AllowsInApp(null, kind)));
    }

    [Fact]
    public void SwitchingAGroupOffStopsItsNotifications()
    {
        NotificationPreference preference = NotificationPreferenceRules.Defaults();
        preference.Groups["documents"] = new ChannelChoice { InApp = false };

        Assert.False(NotificationPreferenceRules.AllowsInApp(
            preference, NotificationKind.ContractExpiring));
        Assert.False(NotificationPreferenceRules.AllowsInApp(
            preference, NotificationKind.DocumentExpiring));
        Assert.False(NotificationPreferenceRules.AllowsInApp(
            preference, NotificationKind.AcknowledgementOutstanding));
    }

    [Fact]
    public void SwitchingOneGroupOffLeavesTheOthersAlone()
    {
        NotificationPreference preference = NotificationPreferenceRules.Defaults();
        preference.Groups["documents"] = new ChannelChoice { InApp = false };

        Assert.True(NotificationPreferenceRules.AllowsInApp(
            preference, NotificationKind.TimesheetDue));
        Assert.True(NotificationPreferenceRules.AllowsInApp(
            preference, NotificationKind.ReviewOverdue));
    }

    // A row saved before a group existed has no cell for it. That is the
    // default, not silence.
    [Fact]
    public void AGroupMissingFromASavedRowFallsBackToTheDefault()
    {
        NotificationPreference preference = new();

        Assert.True(NotificationPreferenceRules.AllowsInApp(
            preference, NotificationKind.ReviewOverdue));
    }

    // ── defaults ───────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultsCoverEveryGroup()
    {
        NotificationPreference defaults = NotificationPreferenceRules.Defaults();

        Assert.All(
            NotificationGroups.All,
            group => Assert.True(defaults.Groups.ContainsKey(group.Key)));
    }

    [Fact]
    public void TheDefaultsNeverSwitchOnAChannelThatCannotDeliver()
    {
        NotificationPreference defaults = NotificationPreferenceRules.Defaults();

        Assert.All(defaults.Groups.Values, choice =>
        {
            Assert.False(choice.Push);
            Assert.False(choice.Email);
        });
    }

    [Fact]
    public void ADigestIsDailyUnlessSomebodySaysOtherwise()
    {
        Assert.Equal(DigestFrequency.Daily, NotificationPreferenceRules.Defaults().DigestFrequency);
    }

    // ── the zone ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Africa/Lagos")]
    [InlineData("Europe/London")]
    [InlineData(null)]
    [InlineData("")]
    public void AKnownZoneOrNoneIsFine(string? zone)
    {
        Assert.True(NotificationPreferenceRules.IsKnownZone(zone));
    }

    // A stored "GMT+1" would throw wherever it is first used, which is a
    // background digest rather than the screen that set it.
    [Theory]
    [InlineData("GMT+1")]
    [InlineData("Lagos")]
    [InlineData("Mars/Olympus")]
    public void SomethingThatIsNotAZoneIsRefused(string zone)
    {
        Assert.False(NotificationPreferenceRules.IsKnownZone(zone));
    }
}
