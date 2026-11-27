using Motee.Domain.Approvals;

namespace Motee.Tests.Unit;

// Raising a request of a type that has no module of its own.
//
// The rule that matters is which types intake refuses. A module-driven type is one
// where something creates the record first and then asks for a decision on it — leave
// reserves days against a balance, a loan computes a repayment schedule. Raising one of
// those through a generic form would produce an approval with nothing behind it:
// approved leave that was never booked, which the settlement observer skips in silence
// because there is no request to update.
//
// The frontend's own intake form currently offers those types. This is the backend
// disagreeing with it on purpose.
public class ApprovalIntakeTests
{
    [Theory]
    [InlineData("leave_request")]
    [InlineData("expense_claim")]
    [InlineData("loan_request")]
    [InlineData("onboarding")]
    [InlineData("workforce_request")]
    public void AModuleDrivenTypeCannotBeRaisedFromIntake(string documentType) =>
        Assert.True(ApprovalIntake.IsModuleDriven(documentType));

    // The six that were decorative. Intake is what makes them work, because for these
    // the approval is the record.
    [Theory]
    [InlineData("job_requisition")]
    [InlineData("contract")]
    [InlineData("offboarding_clearance")]
    [InlineData("promotion_request")]
    [InlineData("training_request")]
    [InlineData("asset_request")]
    public void ATypeWithNoModuleCanBeRaised(string documentType) =>
        Assert.False(ApprovalIntake.IsModuleDriven(documentType));

    // A tenant's own category — "Travel authorisation" — is raisable like any other
    // type with no module behind it.
    [Fact]
    public void ATenantsOwnTypeCanBeRaised() =>
        Assert.False(ApprovalIntake.IsModuleDriven("travel_authorisation"));

    [Fact]
    public void TheCheckIgnoresCase() =>
        Assert.True(ApprovalIntake.IsModuleDriven("Leave_Request"));

    // A schema for a type intake refuses would be a form nobody can submit. If a sixth
    // module starts using the engine, this is the test that catches the schema left
    // behind for it.
    [Fact]
    public void NoTypeWithFieldsIsModuleDriven() =>
        Assert.All(
            ApprovalIntake.TypesWithFields,
            type => Assert.False(ApprovalIntake.IsModuleDriven(type)));

    // ── the field schemas ───────────────────────────────────────────────────

    [Fact]
    public void AKnownTypeCarriesItsFields()
    {
        IReadOnlyList<IntakeField> fields = ApprovalIntake.FieldsFor("asset_request");

        Assert.Equal(3, fields.Count);

        IntakeField type = fields[0];

        Assert.Equal("assetType", type.Key);
        Assert.Equal("Asset type", type.Label);
        Assert.Equal(IntakeFieldKind.Choice, type.Kind);
        Assert.Contains("Laptop", type.Options!);
        Assert.True(type.Required);
    }

    // A generic request: a title, a reason and an attachment. Not an error — a tenant
    // category nobody wrote a schema for is still worth raising.
    [Fact]
    public void AnUnknownTypeHasNoExtraFields() =>
        Assert.Empty(ApprovalIntake.FieldsFor("travel_authorisation"));

    // Options only where the list is a domain enum. Anything backed by a table — a
    // department, a counterparty — is Text, because serving the list here would be a
    // second copy of /departments that drifts from it.
    [Fact]
    public void OnlyChoiceFieldsCarryOptions()
    {
        foreach (string type in ApprovalIntake.TypesWithFields)
        {
            foreach (IntakeField field in ApprovalIntake.FieldsFor(type))
            {
                if (field.Kind == IntakeFieldKind.Choice)
                {
                    Assert.NotNull(field.Options);
                    Assert.NotEmpty(field.Options);
                }
                else
                {
                    Assert.Null(field.Options);
                }
            }
        }
    }

    [Fact]
    public void EveryFieldIsNamedOnceWithinItsType() =>
        Assert.All(ApprovalIntake.TypesWithFields, type =>
        {
            IReadOnlyList<IntakeField> fields = ApprovalIntake.FieldsFor(type);

            Assert.Equal(
                fields.Count,
                fields.Select(field => field.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
        });

    // ── checking what was sent ──────────────────────────────────────────────

    private static Dictionary<string, string?> AssetRequest() => new()
    {
        ["assetType"] = "Laptop",
        ["model"] = "MacBook Pro 14",
        ["justification"] = "Current machine is five years old.",
    };

    [Fact]
    public void ACompleteSubmissionPasses() =>
        Assert.Equal(
            ApprovalIntake.FieldRefusal.None,
            ApprovalIntake.Check("asset_request", AssetRequest()).Refusal);

    [Fact]
    public void AMissingRequiredFieldIsNamed()
    {
        Dictionary<string, string?> values = AssetRequest();
        values["justification"] = "   ";

        (string field, ApprovalIntake.FieldRefusal refusal) =
            ApprovalIntake.Check("asset_request", values);

        Assert.Equal(ApprovalIntake.FieldRefusal.Missing, refusal);

        // Named, so the refusal can say which box to fill rather than "invalid request".
        Assert.Equal("justification", field);
    }

    [Fact]
    public void AnOptionalFieldMayBeOmitted()
    {
        Dictionary<string, string?> values = AssetRequest();
        values.Remove("model");

        Assert.Equal(
            ApprovalIntake.FieldRefusal.None,
            ApprovalIntake.Check("asset_request", values).Refusal);
    }

    [Fact]
    public void AChoiceOutsideItsOptionsIsRefused()
    {
        Dictionary<string, string?> values = AssetRequest();
        values["assetType"] = "Helicopter";

        Assert.Equal(
            ApprovalIntake.FieldRefusal.NotAnOption,
            ApprovalIntake.Check("asset_request", values).Refusal);
    }

    [Fact]
    public void ANumberThatIsNotANumberIsRefused()
    {
        Dictionary<string, string?> values = new()
        {
            ["positionTitle"] = "Site Engineer",
            ["openings"] = "a few",
        };

        Assert.Equal(
            ApprovalIntake.FieldRefusal.NotANumber,
            ApprovalIntake.Check("job_requisition", values).Refusal);
    }

    [Fact]
    public void ADateMustBeIso()
    {
        Dictionary<string, string?> values = new()
        {
            ["lastDay"] = "31/12/2026",
        };

        Assert.Equal(
            ApprovalIntake.FieldRefusal.NotADate,
            ApprovalIntake.Check("offboarding_clearance", values).Refusal);

        values["lastDay"] = "2026-12-31";

        Assert.Equal(
            ApprovalIntake.FieldRefusal.None,
            ApprovalIntake.Check("offboarding_clearance", values).Refusal);
    }

    // Refused rather than stored. The field map is rendered back to the approver, so
    // anything accepted here appears on the request as though it had been asked for.
    [Fact]
    public void AFieldNobodyAskedForIsRefused()
    {
        Dictionary<string, string?> values = AssetRequest();
        values["approvedBy"] = "me";

        (string field, ApprovalIntake.FieldRefusal refusal) =
            ApprovalIntake.Check("asset_request", values);

        Assert.Equal(ApprovalIntake.FieldRefusal.Unknown, refusal);
        Assert.Equal("approvedBy", field);
    }

    [Fact]
    public void AValueTooLongIsRefused()
    {
        Dictionary<string, string?> values = AssetRequest();
        values["justification"] = new string('a', 500);

        Assert.Equal(
            ApprovalIntake.FieldRefusal.TooLong,
            ApprovalIntake.Check("asset_request", values).Refusal);
    }

    // A type with no schema takes no fields at all, so sending some is a mistake worth
    // naming rather than silently dropping.
    [Fact]
    public void FieldsSentForATypeWithNoSchemaAreRefused() =>
        Assert.Equal(
            ApprovalIntake.FieldRefusal.Unknown,
            ApprovalIntake.Check(
                "travel_authorisation",
                new Dictionary<string, string?> { ["destination"] = "Abuja" }).Refusal);

    [Fact]
    public void SubmittingNothingForATypeWithNoSchemaPasses() =>
        Assert.Equal(
            ApprovalIntake.FieldRefusal.None,
            ApprovalIntake.Check("travel_authorisation", new Dictionary<string, string?>())
                .Refusal);
}
