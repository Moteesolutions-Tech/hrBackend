using Motee.Domain.Settings;

namespace Motee.Tests.Settings;

// The dropdown options a company configures.
//
// The panel offers pick lists for Department, Job Title and Location — all
// three of which are already tables. Storing them again here would give the
// product two lists of departments that disagree, and adding one in Settings
// would not create a department, so the employee form would never offer it.
//
// Written before the rules.
public class PickListTests
{
    private static PickListDefinition Configured => Require("asset-category");

    private static PickListDefinition EntityBacked => Require("department");

    private static PickListDefinition Require(string key)
    {
        PickListDefinition? list = PickListCatalogue.Find(key);
        Assert.NotNull(list);
        return list;
    }

    private static PickListOption Option(string value, bool isDefault = false, int sequence = 1) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ListKey = "asset-category",
            Value = value,
            IsDefault = isDefault,
            Sequence = sequence,
        };

    // ── the catalogue ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("department")]
    [InlineData("job-title")]
    [InlineData("location")]
    public void AListThatIsAlreadyATableIsNotEditedHere(string key)
    {
        PickListDefinition list = Require(key);

        Assert.Equal(PickListSource.Entity, list.Source);
        Assert.False(string.IsNullOrWhiteSpace(list.OwnedBy));
        Assert.False(string.IsNullOrWhiteSpace(list.OwnerRoute));
    }

    [Fact]
    public void AnEntityBackedListRefusesWritesAndNamesWhereToGo()
    {
        Assert.Equal(
            PickListRefusal.ListNotConfigurable,
            PickListRules.CheckValue(EntityBacked, "Engineering", []));

        Assert.Equal("/organization/departments", EntityBacked.OwnerRoute);
    }

    [Fact]
    public void AFreeTextFieldIsWhatAPickListIsFor()
    {
        Assert.Equal(PickListSource.Configured, Configured.Source);
    }

    [Fact]
    public void AListThatIsNotInTheCatalogueIsNotAList()
    {
        Assert.Null(PickListCatalogue.Find("favourite-colour"));
        Assert.Null(PickListCatalogue.Find(null));
        Assert.Null(PickListCatalogue.Find("  "));
    }

    // ── values ─────────────────────────────────────────────────────────────

    [Fact]
    public void AValueIsRequired()
    {
        Assert.Equal(
            PickListRefusal.ValueRequired, PickListRules.CheckValue(Configured, "   ", []));
    }

    [Fact]
    public void AValueHasACeiling()
    {
        string tooLong = new('x', PickListRules.MaximumValueLength + 1);

        Assert.Equal(
            PickListRefusal.ValueTooLong, PickListRules.CheckValue(Configured, tooLong, []));
    }

    [Fact]
    public void AGoodValueIsAccepted()
    {
        Assert.Equal(
            PickListRefusal.None, PickListRules.CheckValue(Configured, "Laptop", []));
    }

    // A list holding both "Laptop" and "laptop" is a list somebody has to look
    // at twice to use once, and the register cannot be grouped by it.
    [Fact]
    public void TheSameValueInAnotherCaseIsADuplicate()
    {
        List<PickListOption> existing = [Option("Laptop", isDefault: true)];

        Assert.Equal(
            PickListRefusal.DuplicateValue,
            PickListRules.CheckValue(Configured, "laptop", existing));
    }

    [Fact]
    public void RenamingAnOptionDoesNotCollideWithItself()
    {
        PickListOption existing = Option("Laptop", isDefault: true);

        Assert.Equal(
            PickListRefusal.None,
            PickListRules.CheckValue(Configured, "LAPTOP", [existing], editing: existing.Id));
    }

    [Fact]
    public void SurroundingSpaceIsNotANewValue()
    {
        List<PickListOption> existing = [Option("Laptop", isDefault: true)];

        Assert.Equal(
            PickListRefusal.DuplicateValue,
            PickListRules.CheckValue(Configured, "  Laptop  ", existing));
    }

    // ── exactly one default ────────────────────────────────────────────────

    [Fact]
    public void TheFirstOptionOnAnEmptyListBecomesTheDefault()
    {
        List<PickListOption> options = [Option("Laptop")];

        PickListRules.Settle(options);

        Assert.True(options[0].IsDefault);
    }

    [Fact]
    public void RemovingTheDefaultPromotesTheNextOption()
    {
        List<PickListOption> options =
        [
            Option("Monitor", sequence: 2),
            Option("Phone", sequence: 3),
        ];

        PickListRules.Settle(options);

        Assert.True(options[0].IsDefault);
        Assert.False(options[1].IsDefault);
    }

    [Fact]
    public void TwoDefaultsLandAsOne()
    {
        List<PickListOption> options =
        [
            Option("Laptop", isDefault: true, sequence: 1),
            Option("Monitor", isDefault: true, sequence: 2),
        ];

        PickListRules.Settle(options);

        Assert.Single(options, option => option.IsDefault);
    }

    [Fact]
    public void AnExistingDefaultIsLeftAlone()
    {
        List<PickListOption> options =
        [
            Option("Laptop", sequence: 1),
            Option("Monitor", isDefault: true, sequence: 2),
        ];

        PickListRules.Settle(options);

        Assert.False(options[0].IsDefault);
        Assert.True(options[1].IsDefault);
    }

    [Fact]
    public void AnEmptyListHasNoDefaultToSettle()
    {
        List<PickListOption> options = [];

        PickListRules.Settle(options);

        Assert.Empty(options);
    }

    // ── order ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheFirstOptionSortsFirstRatherThanAtZero()
    {
        Assert.Equal(1, PickListRules.NextSequence([]));
    }

    [Fact]
    public void ANewOptionGoesOnTheEnd()
    {
        List<PickListOption> options =
        [
            Option("Laptop", sequence: 1),
            Option("Monitor", sequence: 7),
        ];

        Assert.Equal(8, PickListRules.NextSequence(options));
    }
}
