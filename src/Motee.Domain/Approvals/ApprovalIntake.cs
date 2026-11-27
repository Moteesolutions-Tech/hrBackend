namespace Motee.Domain.Approvals;

// Raising a request of a type that has no module of its own.
//
// Six of the eleven built-in document types were decorative: a tenant could save a
// chain against "promotion_request" and nothing would ever start one. Intake is what
// makes them work, and the reason it can is that for these types the approval *is* the
// record. There is no promotion row waiting to be approved — the request, its fields and
// its decision are the whole thing.
//
// Which is also why the other five are refused. A module-driven type is one where
// something creates the record first and then asks for a decision on it: leave reserves
// days against a balance, an expense claim takes a reference, a loan computes a
// repayment schedule. Raising one of those through a generic form would produce an
// approval with nothing behind it — approved leave that was never booked, which the
// settlement observer would skip in silence because there is no request to update.
public enum IntakeFieldKind
{
    Text,
    Number,
    Date,

    // One of a fixed list. Only used where the options are a domain enum: anything
    // backed by a table — a department, a counterparty — is served as Text, because
    // serving the list here would be a second copy of /departments that drifts from it.
    Choice,
}

public sealed record IntakeField
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required IntakeFieldKind Kind { get; init; }

    // Set only for Choice. Null for everything else, which is how a client knows to
    // draw a box rather than a dropdown.
    public IReadOnlyList<string>? Options { get; init; }

    public bool Required { get; init; }
}

public static class ApprovalIntake
{
    public const int LongestTitle = 200;

    public const int LongestValue = 400;

    // The subject type every intake request carries.
    //
    // Self-referential: SubjectId is the instance's own id, because the approval is the
    // record. The engine insists every instance has a subject, and pointing one at
    // nothing would leave a row claiming to be about something that does not exist.
    public const string SubjectType = "approval_request";

    // Types a module starts chains for. Found by what calls IApprovalService.StartAsync,
    // not by a hand-kept list — if a sixth module starts using the engine, this is the
    // line that has to change, and the test below says so.
    public static readonly IReadOnlyList<string> ModuleDriven =
    [
        ApprovalDocumentTypes.LeaveRequest,
        ApprovalDocumentTypes.ExpenseClaim,
        ApprovalDocumentTypes.LoanRequest,
        ApprovalDocumentTypes.Onboarding,
        ApprovalDocumentTypes.WorkforceRequest,
    ];

    public static bool IsModuleDriven(string? documentType) =>
        documentType is not null
        && ModuleDriven.Contains(documentType.Trim(), StringComparer.OrdinalIgnoreCase);

    // What each type asks for, beyond a title and a reason.
    //
    // Mirrors the intake form's own field list, which was a constant in the component —
    // so the form keeps working, and the definition now lives where the validation is.
    // A type nobody has a schema for gets none, which is a perfectly good generic
    // request: a title, a reason and an attachment.
    private static readonly Dictionary<string, IReadOnlyList<IntakeField>> Schemas =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [ApprovalDocumentTypes.JobRequisition] =
            [
                Text("positionTitle", "Position title", required: true),
                Text("department", "Department"),
                Number("openings", "Openings", required: true),
                Text("salaryRange", "Salary range"),
            ],

            [ApprovalDocumentTypes.Contract] =
            [
                Text("counterparty", "Counterparty", required: true),
                Choice("contractType", "Type",
                    ["Employment", "Service", "Supplier", "NDA", "Consultancy"]),
                Date("startDate", "Start date", required: true),
                Date("endDate", "End date"),
            ],

            [ApprovalDocumentTypes.OffboardingClearance] =
            [
                Date("lastDay", "Last working day", required: true),
                Choice("reason", "Reason",
                    ["Resignation", "Dismissal", "Redundancy", "Retirement", "End of contract"]),
            ],

            [ApprovalDocumentTypes.PromotionRequest] =
            [
                Text("newTitle", "Proposed new title", required: true),
                Text("newGrade", "Proposed grade"),
                Number("newSalary", "Proposed salary"),
                Date("effectiveDate", "Effective from", required: true),
            ],

            [ApprovalDocumentTypes.TrainingRequest] =
            [
                Text("course", "Course or programme", required: true),
                Text("provider", "Provider"),
                Number("cost", "Estimated cost"),
                Date("startDate", "Start date"),
            ],

            [ApprovalDocumentTypes.AssetRequest] =
            [
                Choice("assetType", "Asset type",
                    ["Laptop", "Phone", "Monitor", "Desk", "Vehicle", "Other"], required: true),
                Text("model", "Preferred model"),
                Text("justification", "Justification", required: true),
            ],
        };

    public static IReadOnlyList<IntakeField> FieldsFor(string? documentType) =>
        documentType is not null
        && Schemas.TryGetValue(documentType.Trim(), out IReadOnlyList<IntakeField>? fields)
            ? fields
            : [];

    // Every type with a schema, so a test can assert none of them is module-driven —
    // a schema for a type intake refuses is a form nobody can submit.
    public static IReadOnlyList<string> TypesWithFields => [.. Schemas.Keys];

    public enum FieldRefusal
    {
        None,
        Missing,
        NotANumber,
        NotADate,
        NotAnOption,
        TooLong,

        // A key nobody asked for. Refused rather than stored, because a field map is
        // rendered back to the approver — and anything accepted here appears on the
        // request as though it had been asked for.
        Unknown,
    }

    // Checks one submitted value against the schema. Returns the field it failed on, so
    // the refusal can name it instead of saying "invalid request".
    public static (string Field, FieldRefusal Refusal) Check(
        string? documentType,
        IReadOnlyDictionary<string, string?> values)
    {
        IReadOnlyList<IntakeField> schema = FieldsFor(documentType);

        foreach (string key in values.Keys)
        {
            if (!schema.Any(field =>
                string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                return (key, FieldRefusal.Unknown);
            }
        }

        foreach (IntakeField field in schema)
        {
            string? value = Value(values, field.Key);

            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required)
                {
                    return (field.Key, FieldRefusal.Missing);
                }

                continue;
            }

            if (value.Trim().Length > LongestValue)
            {
                return (field.Key, FieldRefusal.TooLong);
            }

            FieldRefusal refusal = CheckValue(field, value.Trim());

            if (refusal != FieldRefusal.None)
            {
                return (field.Key, refusal);
            }
        }

        return (string.Empty, FieldRefusal.None);
    }

    private static FieldRefusal CheckValue(IntakeField field, string value) => field.Kind switch
    {
        IntakeFieldKind.Number => decimal.TryParse(
            value,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out _)
            ? FieldRefusal.None
            : FieldRefusal.NotANumber,

        // ISO only, like every other date on the wire here, so the value stored and the
        // value meant are the same day whatever the sender's locale.
        IntakeFieldKind.Date => DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _)
            ? FieldRefusal.None
            : FieldRefusal.NotADate,

        IntakeFieldKind.Choice =>
            field.Options?.Contains(value, StringComparer.OrdinalIgnoreCase) == true
                ? FieldRefusal.None
                : FieldRefusal.NotAnOption,

        _ => FieldRefusal.None,
    };

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.FirstOrDefault(pair =>
            string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static IntakeField Text(string key, string label, bool required = false) =>
        new() { Key = key, Label = label, Kind = IntakeFieldKind.Text, Required = required };

    private static IntakeField Number(string key, string label, bool required = false) =>
        new() { Key = key, Label = label, Kind = IntakeFieldKind.Number, Required = required };

    private static IntakeField Date(string key, string label, bool required = false) =>
        new() { Key = key, Label = label, Kind = IntakeFieldKind.Date, Required = required };

    private static IntakeField Choice(
        string key,
        string label,
        IReadOnlyList<string> options,
        bool required = false) =>
        new()
        {
            Key = key,
            Label = label,
            Kind = IntakeFieldKind.Choice,
            Options = options,
            Required = required,
        };
}
