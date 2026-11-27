using System.Globalization;

namespace Motee.Domain.Employees;

// Which details an employee may ask to have changed about themselves.
//
// A whitelist, and that is the whole design. The screen names a field as a dot-path —
// "firstName", "emergencyContactPhone" — so the obvious implementation reflects that
// path onto the entity. The obvious implementation is a mass-assignment hole: a request
// for "status" approves somebody off probation, one for "managerId" re-points who signs
// off their leave, one for "startDate" rewrites their service. Nothing outside this
// catalogue can be requested, and nothing outside it can be written.
//
// Six of the seven groups the profile screen offers are here. Two things are not, and
// are absent for reasons worth stating rather than leaving somebody to infer:
//
//   * Work pattern. A foreign key to a work schedule, not a value somebody types. It
//     changes through /attendance/schedules, where the pattern itself is validated —
//     accepting "Mon-Thu" as free text here would record something nothing can apply.
//   * The work email. It is the account's login, so changing it would desync the
//     employee record from the account that signs in as them, and an email change needs
//     its own verification rather than an HR approval.
//
// Bank details are included deliberately, despite being the obvious fraud target: a
// request to redirect wages is the thing an attacker with a stolen password would send.
// That is an argument for this route rather than against it. A change request carries a
// reason and evidence and cannot take effect until somebody in HR approves it, which is
// a stronger control than letting the employee edit the field directly.
public enum ProfileFieldGroup
{
    Personal,
    Contact,
    Address,
    Emergency,
    Bank,
    Identity,
}

public enum ProfileFieldOutcome
{
    Applied,

    // Sent for a field that must have a value.
    Required,

    // Longer than the column.
    TooLong,

    NotADate,

    // A date that parses and cannot be true, like a birthday next year.
    OutOfRange,
}

// An employee and the one-per-person blocks beside them.
//
// The blocks are non-null on purpose. Bank details and identity documents are created
// on demand, so the catalogue never has to decide what reading a field means when the
// row does not exist yet — the caller ensures they are there, and every entry below can
// just read and write.
public sealed record EmployeeProfile(
    Employee Employee,
    EmployeeBankDetails Bank,
    EmployeeIdentityDocuments Identity);

// One requestable field: what it is called, where it sits, how to read it and how to
// write it. The read and write live beside each other on purpose — a catalogue where
// they are declared apart is one where a field can be requested and then applied to the
// wrong column.
public sealed record ProfileField
{
    public required string Key { get; init; }

    // What the request says it is changing, so the log reads "Preferred name" rather
    // than "preferredName". Sent to the client rather than translated there, because the
    // list of fields and their names belong together.
    public required string Label { get; init; }

    public required ProfileFieldGroup Group { get; init; }

    public required Func<EmployeeProfile, string?> Read { get; init; }

    public required Func<EmployeeProfile, string?, ProfileFieldOutcome> Apply { get; init; }
}

public static class ProfileFields
{
    // Matching the column widths in the configurations. Checked here rather than left
    // to the database, so an over-long value is a refusal somebody can read instead of
    // a 500 from a truncated insert.
    private const int ShortText = 100;

    private const int LongText = 400;

    public static readonly IReadOnlyList<ProfileField> Catalogue =
    [
        // ── personal ────────────────────────────────────────────────────────
        Text("firstName", "First name", ProfileFieldGroup.Personal,
            profile => profile.Employee.FirstName,
            (profile, value) => profile.Employee.FirstName = value!,
            required: true),

        Text("middleName", "Middle name", ProfileFieldGroup.Personal,
            profile => profile.Employee.MiddleName,
            (profile, value) => profile.Employee.MiddleName = value),

        Text("lastName", "Last name", ProfileFieldGroup.Personal,
            profile => profile.Employee.LastName,
            (profile, value) => profile.Employee.LastName = value!,
            required: true),

        Text("title", "Title", ProfileFieldGroup.Personal,
            profile => profile.Employee.Title,
            (profile, value) => profile.Employee.Title = value),

        Text("preferredName", "Preferred name", ProfileFieldGroup.Personal,
            profile => profile.Employee.PreferredName,
            (profile, value) => profile.Employee.PreferredName = value),

        Text("maidenName", "Maiden name", ProfileFieldGroup.Personal,
            profile => profile.Employee.MaidenName,
            (profile, value) => profile.Employee.MaidenName = value),

        Date("dateOfBirth", "Date of birth", ProfileFieldGroup.Personal,
            profile => profile.Employee.DateOfBirth,
            (profile, value) => profile.Employee.DateOfBirth = value,

            // Nobody is born tomorrow, and a mistyped year would quietly skew every
            // age and long-service figure that reads this.
            future: false),

        Text("gender", "Gender", ProfileFieldGroup.Personal,
            profile => profile.Employee.Gender,
            (profile, value) => profile.Employee.Gender = value),

        Text("nationality", "Nationality", ProfileFieldGroup.Personal,
            profile => profile.Employee.Nationality,
            (profile, value) => profile.Employee.Nationality = value),

        Text("maritalStatus", "Marital status", ProfileFieldGroup.Personal,
            profile => profile.Employee.MaritalStatus,
            (profile, value) => profile.Employee.MaritalStatus = value),

        Text("ethnicity", "Ethnicity", ProfileFieldGroup.Personal,
            profile => profile.Employee.Ethnicity,
            (profile, value) => profile.Employee.Ethnicity = value),

        // ── contact ─────────────────────────────────────────────────────────
        Text("phone", "Phone number", ProfileFieldGroup.Contact,
            profile => profile.Employee.Phone,
            (profile, value) => profile.Employee.Phone = value),

        // ── address ─────────────────────────────────────────────────────────
        //
        // One address, not the screen's three. There is one column, and offering
        // "correspondence address" as a requestable field would accept a request that
        // could never be applied anywhere.
        Text("address", "Address", ProfileFieldGroup.Address,
            profile => profile.Employee.Address,
            (profile, value) => profile.Employee.Address = value,
            length: LongText),

        Text("state", "State or county", ProfileFieldGroup.Address,
            profile => profile.Employee.State,
            (profile, value) => profile.Employee.State = value),

        // ── emergency contact ───────────────────────────────────────────────
        Text("emergencyContactName", "Emergency contact name", ProfileFieldGroup.Emergency,
            profile => profile.Employee.EmergencyContactName,
            (profile, value) => profile.Employee.EmergencyContactName = value),

        Text("emergencyContactRelationship", "Emergency contact relationship",
            ProfileFieldGroup.Emergency,
            profile => profile.Employee.EmergencyContactRelationship,
            (profile, value) => profile.Employee.EmergencyContactRelationship = value),

        Text("emergencyContactPhone", "Emergency contact phone", ProfileFieldGroup.Emergency,
            profile => profile.Employee.EmergencyContactPhone,
            (profile, value) => profile.Employee.EmergencyContactPhone = value),

        Text("emergencyContactEmail", "Emergency contact email", ProfileFieldGroup.Emergency,
            profile => profile.Employee.EmergencyContactEmail,
            (profile, value) => profile.Employee.EmergencyContactEmail = value),

        // ── bank ────────────────────────────────────────────────────────────
        Text("bankName", "Bank name", ProfileFieldGroup.Bank,
            profile => profile.Bank.BankName,
            (profile, value) => profile.Bank.BankName = value),

        Text("accountHolderName", "Account holder name", ProfileFieldGroup.Bank,
            profile => profile.Bank.AccountHolderName,
            (profile, value) => profile.Bank.AccountHolderName = value),

        Text("accountNumber", "Account number", ProfileFieldGroup.Bank,
            profile => profile.Bank.AccountNumber,
            (profile, value) => profile.Bank.AccountNumber = value),

        Text("sortCode", "Sort code", ProfileFieldGroup.Bank,
            profile => profile.Bank.SortCode,
            (profile, value) => profile.Bank.SortCode = value),

        // ── identity and tax ────────────────────────────────────────────────
        //
        // Identifiers, not quantities: stored and compared as text, because a NIN is
        // eleven digits that must keep a leading zero.
        Text("nationalIdNumber", "National ID number", ProfileFieldGroup.Identity,
            profile => profile.Identity.NationalIdNumber,
            (profile, value) => profile.Identity.NationalIdNumber = value),

        Text("taxIdNumber", "Tax ID number", ProfileFieldGroup.Identity,
            profile => profile.Identity.TaxIdNumber,
            (profile, value) => profile.Identity.TaxIdNumber = value),

        Text("pensionId", "Pension ID", ProfileFieldGroup.Identity,
            profile => profile.Identity.PensionId,
            (profile, value) => profile.Identity.PensionId = value),

        Text("housingFundNumber", "Housing fund number", ProfileFieldGroup.Identity,
            profile => profile.Identity.HousingFundNumber,
            (profile, value) => profile.Identity.HousingFundNumber = value),

        Text("drivingLicenceNumber", "Driving licence number", ProfileFieldGroup.Identity,
            profile => profile.Identity.DrivingLicenceNumber,
            (profile, value) => profile.Identity.DrivingLicenceNumber = value),

        Date("drivingLicenceExpiry", "Driving licence expiry", ProfileFieldGroup.Identity,
            profile => profile.Identity.DrivingLicenceExpiry,
            (profile, value) => profile.Identity.DrivingLicenceExpiry = value),

        Text("passportNumber", "Passport number", ProfileFieldGroup.Identity,
            profile => profile.Identity.PassportNumber,
            (profile, value) => profile.Identity.PassportNumber = value),

        Date("passportExpiry", "Passport expiry", ProfileFieldGroup.Identity,
            profile => profile.Identity.PassportExpiry,
            (profile, value) => profile.Identity.PassportExpiry = value),

        Text("passportIssuingCountry", "Passport issuing country", ProfileFieldGroup.Identity,
            profile => profile.Identity.PassportIssuingCountry,
            (profile, value) => profile.Identity.PassportIssuingCountry = value),
    ];

    // Case-insensitive, because the screen sends camelCase paths and nobody should be
    // refused over a capital letter. Null for anything not in the catalogue, which is
    // the refusal every caller checks.
    public static ProfileField? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : Catalogue.FirstOrDefault(field =>
                string.Equals(field.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    private static ProfileField Text(
        string key,
        string label,
        ProfileFieldGroup group,
        Func<EmployeeProfile, string?> read,
        Action<EmployeeProfile, string?> write,
        bool required = false,
        int length = ShortText) =>
        new()
        {
            Key = key,
            Label = label,
            Group = group,
            Read = read,
            Apply = (profile, value) =>
            {
                // Trimmed first, so "Ada " and "Ada" are not two different requests and
                // whitespace cannot pass as a value for a required field.
                string? trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

                if (trimmed is null)
                {
                    if (required)
                    {
                        return ProfileFieldOutcome.Required;
                    }

                    write(profile, null);

                    return ProfileFieldOutcome.Applied;
                }

                if (trimmed.Length > length)
                {
                    return ProfileFieldOutcome.TooLong;
                }

                write(profile, trimmed);

                return ProfileFieldOutcome.Applied;
            },
        };

    private static ProfileField Date(
        string key,
        string label,
        ProfileFieldGroup group,
        Func<EmployeeProfile, DateOnly?> read,
        Action<EmployeeProfile, DateOnly?> write,
        bool future = true) =>
        new()
        {
            Key = key,
            Label = label,
            Group = group,

            // ISO on the way out, so the value stored on the request and the value
            // applied are the same string whatever the viewer's locale — and so a
            // stored request cannot be read back as a different day.
            Read = profile =>
                read(profile)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Apply = (profile, value) =>
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    write(profile, null);

                    return ProfileFieldOutcome.Applied;
                }

                if (!DateOnly.TryParseExact(
                        value.Trim(),
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateOnly parsed))
                {
                    return ProfileFieldOutcome.NotADate;
                }

                // Against UTC rather than the tenant's day. A birthday is not
                // timezone-sensitive, and a date a day either side of the boundary is
                // not the mistake this is catching.
                if (!future && parsed > DateOnly.FromDateTime(DateTime.UtcNow))
                {
                    return ProfileFieldOutcome.OutOfRange;
                }

                write(profile, parsed);

                return ProfileFieldOutcome.Applied;
            },
        };

    // Initials are deliberately not touched by a name change.
    //
    // FullName is a computed property and follows a rename on its own. Initials is a
    // stored column the employee form sets by hand, so somebody may have chosen "A.O."
    // over "AO" — and a request to change a surname carries no mandate to rewrite that.
    // It is separately editable, which is where a stale pair gets corrected.
}
