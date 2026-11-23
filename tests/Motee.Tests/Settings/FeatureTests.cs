using Motee.Domain.Authorization;
using Motee.Domain.Settings;
using Motee.Domain.Tenants;

namespace Motee.Tests.Settings;

// Which parts of the product a company has switched on, and what switching one
// off actually closes.
//
// Two screens claimed to do this before anything read either: the onboarding
// wizard writes tenants.settings.enabled_modules, and the settings panel has an
// Enabled switch per module. Both were inert, and they did not even agree what
// the modules were called — the wizard stores "learning", the panel's row is
// "mod-training".
//
// Written before the rules.
public class FeatureTests
{
    // ── the catalogue holds together ────────────────────────────────────────

    [Fact]
    public void EveryFeatureHasADistinctId()
    {
        Assert.Equal(
            FeatureCatalogue.Ids.Count,
            FeatureCatalogue.Ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryGovernedKeyIsARealModule()
    {
        IEnumerable<string> governed = FeatureCatalogue.All.SelectMany(feature => feature.ModuleKeys);

        Assert.All(governed, key => Assert.Contains(key, ModuleCatalogue.All));
    }

    // One key under two features would mean switching one off half-closes the
    // other, and the half it closed would only show up in production.
    [Fact]
    public void NoModuleKeyIsGovernedTwice()
    {
        List<string> governed = [.. FeatureCatalogue.All.SelectMany(feature => feature.ModuleKeys)];

        Assert.Equal(governed.Count, governed.Distinct(StringComparer.Ordinal).Count());
    }

    // The screen that switches features is the way back from switching one off.
    // Gating it would let a company lock itself out of its own settings.
    [Fact]
    public void SettingsItselfIsNeverGoverned()
    {
        Assert.Null(FeatureCatalogue.GovernorOf("admin.settings"));
    }

    [Theory]
    [InlineData("admin.access-levels")]
    [InlineData("admin.users")]
    [InlineData("admin.audit-trail")]
    [InlineData("organization.employees")]
    public void RunningTheCompanyIsNeverGoverned(string moduleKey)
    {
        Assert.Null(FeatureCatalogue.GovernorOf(moduleKey));
    }

    // ── the two vocabularies reconcile ──────────────────────────────────────

    [Theory]
    [InlineData("learning", "learning")]
    [InlineData("training", "learning")]
    [InlineData("mod-training", "learning")]
    [InlineData("leave-management", "leave-management")]
    [InlineData("mod-leave", "leave-management")]
    [InlineData("LEAVE", "leave-management")]
    public void TheWizardAndThePanelMeanTheSameFeature(string given, string expected)
    {
        Assert.Equal(expected, FeatureCatalogue.Normalise(given));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("mod-nonsense")]
    public void SomethingThatIsNotAFeatureIsNotOne(string? given)
    {
        Assert.Null(FeatureCatalogue.Normalise(given));
    }

    // A typo that round-trips becomes a switch nobody can turn off: it is not in
    // the catalogue, so the screen never shows it, and it sits in the stored
    // list for good.
    [Fact]
    public void AnUnknownIdIsDroppedRatherThanStored()
    {
        FeatureState state = FeatureState.Of(["learning", "not-a-feature"], hidden: []);

        Assert.True(state.IsEnabled("learning"));
        Assert.DoesNotContain("not-a-feature", state.Enabled);
    }

    // The wizard writes its own ids. A tenant set up before this catalogue
    // existed has to keep working.
    [Fact]
    public void AStoredWizardListStillReads()
    {
        TenantSettings stored = new()
        {
            EnabledModules = [.. TenantSetupCatalogue.DefaultModules],
        };

        FeatureState state = FeatureState.Of(stored.EnabledModules, hidden: []);

        Assert.All(
            stored.EnabledModules,
            id => Assert.True(state.IsEnabled(id), $"'{id}' from the wizard did not resolve."));
    }

    // ── what a switch closes ───────────────────────────────────────────────

    [Fact]
    public void AnEnabledFeatureAnswersOnItsOwnModules()
    {
        FeatureState state = FeatureState.Of(["learning"], hidden: []);

        Assert.True(state.Allows("talent.training"));
    }

    [Fact]
    public void ADisabledFeatureStopsAnsweringOnItsOwnModules()
    {
        FeatureState state = FeatureState.Of(["attendance"], hidden: []);

        Assert.False(state.Allows("talent.training"));
    }

    [Fact]
    public void ADisabledFeatureClosesEveryModuleItGoverns()
    {
        FeatureState state = FeatureState.Of(["employee-management"], hidden: []);

        Assert.False(state.Allows("workspace.community"));
        Assert.False(state.Allows("workspace.announcements"));
        Assert.False(state.Allows("workspace.kudos"));
        Assert.False(state.Allows("workspace.suggestions"));
        Assert.False(state.Allows("workspace.surveys"));
    }

    [Fact]
    public void AModuleNoFeatureGovernsIsAlwaysOpen()
    {
        FeatureState state = FeatureState.Of([], hidden: []);

        Assert.True(state.Allows("admin.settings"));
        Assert.True(state.Allows("organization.employees"));
    }

    // ── essentials ─────────────────────────────────────────────────────────

    [Fact]
    public void EmployeeRecordsCannotBeSwitchedOff()
    {
        FeatureState state = FeatureState.Of(["attendance"], hidden: []);

        Assert.True(state.IsEnabled("employee-management"));
    }

    [Fact]
    public void AnEmptyChoiceStillLeavesTheProductRunning()
    {
        FeatureState state = FeatureState.Of([], hidden: []);

        Assert.All(
            FeatureCatalogue.All.Where(feature => feature.Essential),
            feature => Assert.True(state.IsEnabled(feature.Id)));
    }

    [Fact]
    public void AnEssentialFeatureCannotBeHiddenFromTheSidebar()
    {
        FeatureState state = FeatureState.Of(
            FeatureCatalogue.Ids,
            hidden: ["employee-management"]);

        Assert.True(state.ShowsInSidebar("employee-management"));
    }

    // ── the sidebar cannot offer what the server refuses ───────────────────

    [Fact]
    public void ADisabledFeatureIsHiddenWhateverThePayloadSaid()
    {
        FeatureState state = FeatureState.Of(["attendance"], hidden: []);

        Assert.Contains("learning", state.Hidden);
        Assert.False(state.ShowsInSidebar("learning"));
    }

    [Fact]
    public void AnEnabledFeatureCanStillBeHidden()
    {
        FeatureState state = FeatureState.Of(["learning"], hidden: ["learning"]);

        Assert.True(state.IsEnabled("learning"));
        Assert.False(state.ShowsInSidebar("learning"));
    }

    [Fact]
    public void AnEnabledAndUnhiddenFeatureShows()
    {
        FeatureState state = FeatureState.Of(["learning"], hidden: []);

        Assert.True(state.ShowsInSidebar("learning"));
    }

    // ── the switches that govern nothing say so ────────────────────────────

    // Payroll has screens on the frontend and no endpoints here — no pay run, no
    // payslip, no tax. The catalogue carries it with an empty key list so the
    // contract can tell the screen the switch closes nothing, rather than
    // offering one that appears to work.
    [Fact]
    public void PayrollGovernsNothingYet()
    {
        Feature? payroll = FeatureCatalogue.Find("payroll");

        Assert.NotNull(payroll);
        Assert.Empty(payroll.ModuleKeys);
    }

    [Fact]
    public void SwitchingOffAFeatureThatGovernsNothingClosesNothing()
    {
        FeatureState without = FeatureState.Of(
            FeatureCatalogue.Ids.Where(id => id != "payroll"),
            hidden: []);

        Assert.All(ModuleCatalogue.All, key => Assert.True(without.Allows(key)));
    }

    // ── defaults ───────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultsAreWhatANewCompanyGets()
    {
        Assert.Contains("employee-management", FeatureState.Default.Enabled);
        Assert.Contains("leave-management", FeatureState.Default.Enabled);
    }

    [Fact]
    public void EveryDefaultIsAKnownFeature()
    {
        Assert.All(
            FeatureCatalogue.Defaults,
            id => Assert.NotNull(FeatureCatalogue.Find(id)));
    }
}
