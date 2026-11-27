using Motee.Domain.Employees;

namespace Motee.Tests.Unit;

// Which fields an employee may ask to have changed, and what applying one does.
//
// The whitelist is the whole point. A change request names a field, and the frontend
// names it as a dot-path — so the obvious implementation reflects that path onto the
// entity, and the obvious implementation is a mass-assignment hole: somebody requests
// "status" and approves themselves off probation, or "managerId" and reassigns who signs
// off their leave. Nothing outside this catalogue can be requested, and nothing outside
// it can be written.
//
// Written before the service.
public class ProfileFieldTests
{
    // The blocks are part of the profile and never null: bank details and identity
    // documents are created on demand, so the catalogue never has to decide what
    // reading a field means when the row does not exist yet.
    private static EmployeeProfile Ada()
    {
        Guid id = Guid.NewGuid();

        return new EmployeeProfile(
            new Employee
            {
                Id = id,
                FirstName = "Ada",
                LastName = "Okafor",
                Email = "ada@acme.com",
                Phone = "08012345678",
                Status = EmployeeStatus.Active,
                DateOfBirth = new DateOnly(1990, 4, 12),
            },
            new EmployeeBankDetails { Id = Guid.NewGuid(), EmployeeId = id },
            new EmployeeIdentityDocuments { Id = Guid.NewGuid(), EmployeeId = id });
    }

    // ── the catalogue ───────────────────────────────────────────────────────

    [Fact]
    public void EveryFieldIsNamedOnceAndCarriesALabel()
    {
        Assert.NotEmpty(ProfileFields.Catalogue);

        Assert.Equal(
            ProfileFields.Catalogue.Count,
            ProfileFields.Catalogue.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count());

        Assert.All(ProfileFields.Catalogue, field =>
        {
            Assert.False(string.IsNullOrWhiteSpace(field.Key));
            Assert.False(string.IsNullOrWhiteSpace(field.Label));
        });
    }

    [Fact]
    public void TheCatalogueCoversTheGroupsTheScreenOffers()
    {
        // Six of the screen's seven. Work pattern is the one left out, because it is a
        // foreign key to a work schedule rather than a value somebody types — accepting
        // "Mon-Thu" here would record a request nothing could apply.
        Assert.Equal(
            [
                ProfileFieldGroup.Personal,
                ProfileFieldGroup.Contact,
                ProfileFieldGroup.Address,
                ProfileFieldGroup.Emergency,
                ProfileFieldGroup.Bank,
                ProfileFieldGroup.Identity,
            ],
            ProfileFields.Catalogue
                .Select(field => field.Group)
                .Distinct()
                .Order()
                .ToArray());
    }

    // Bank and tax details live on one-per-employee blocks rather than on the employee
    // row, which is easy to miss when reading only Employee.cs — and missing them would
    // have left two of the screen's groups permanently blurred.
    [Theory]
    [InlineData("accountNumber")]
    [InlineData("sortCode")]
    [InlineData("bankName")]
    [InlineData("accountHolderName")]
    [InlineData("nationalIdNumber")]
    [InlineData("taxIdNumber")]
    [InlineData("passportNumber")]
    [InlineData("passportExpiry")]
    public void AFieldOnAProfileBlockIsRequestable(string key) =>
        Assert.NotNull(ProfileFields.Find(key));

    [Fact]
    public void ApplyingWritesToTheBlockRatherThanTheEmployee()
    {
        EmployeeProfile ada = Ada();

        Assert.Equal(ProfileFieldOutcome.Applied,
            ProfileFields.Find("accountNumber")!.Apply(ada, "0123456789"));

        // The leading zero survives, which is the reason these are text and not numbers.
        Assert.Equal("0123456789", ada.Bank.AccountNumber);

        Assert.Equal(ProfileFieldOutcome.Applied,
            ProfileFields.Find("nationalIdNumber")!.Apply(ada, "01234567890"));

        Assert.Equal("01234567890", ada.Identity.NationalIdNumber);
    }

    // A passport expires in the future, unlike a birthday — so the future guard is per
    // field rather than applied to every date.
    [Fact]
    public void APassportExpiryMayBeInTheFuture()
    {
        EmployeeProfile ada = Ada();

        string future = DateOnly.FromDateTime(DateTime.UtcNow)
            .AddYears(5)
            .ToString("yyyy-MM-dd");

        Assert.Equal(ProfileFieldOutcome.Applied,
            ProfileFields.Find("passportExpiry")!.Apply(ada, future));
    }

    [Theory]
    [InlineData("firstName")]
    [InlineData("lastName")]
    [InlineData("preferredName")]
    [InlineData("dateOfBirth")]
    [InlineData("maritalStatus")]
    [InlineData("phone")]
    [InlineData("address")]
    [InlineData("emergencyContactName")]
    [InlineData("emergencyContactPhone")]
    public void ARequestableFieldIsFound(string key) =>
        Assert.NotNull(ProfileFields.Find(key));

    // Case-insensitively, because the screen sends camelCase paths and nobody should
    // be refused over a capital letter.
    [Fact]
    public void FindingAFieldIgnoresCase() =>
        Assert.NotNull(ProfileFields.Find("FirstName"));

    // ── what cannot be requested ────────────────────────────────────────────

    // Each of these is the mass-assignment hole the catalogue exists to close. A
    // change request is a request about somebody's own details, not a way to move
    // themselves through the lifecycle or re-point who approves their leave.
    [Theory]
    [InlineData("status")]
    [InlineData("managerId")]
    [InlineData("departmentId")]
    [InlineData("jobTitle")]
    [InlineData("grade")]
    [InlineData("startDate")]
    [InlineData("dateOfLeaving")]
    [InlineData("employeeNumber")]
    [InlineData("tenantId")]
    [InlineData("id")]
    public void AFieldNobodyMayRequestIsNotFound(string key) =>
        Assert.Null(ProfileFields.Find(key));

    // The work email is the account's login. Changing it would desync the employee
    // record from the account that signs in as them, and an email change needs its own
    // verification rather than an HR approval — so it is not a profile edit.
    [Fact]
    public void TheWorkEmailIsNotRequestable() =>
        Assert.Null(ProfileFields.Find("email"));

    [Fact]
    public void AnUnknownPathIsNotFound()
    {
        Assert.Null(ProfileFields.Find("salary"));
        Assert.Null(ProfileFields.Find("emergencyContacts.0.phone"));
        Assert.Null(ProfileFields.Find(""));
        Assert.Null(ProfileFields.Find(null));
    }

    // ── reading the current value ───────────────────────────────────────────

    [Fact]
    public void TheCurrentValueComesFromTheRecord()
    {
        EmployeeProfile ada = Ada();

        Assert.Equal("Ada", ProfileFields.Find("firstName")!.Read(ada));
        Assert.Equal("08012345678", ProfileFields.Find("phone")!.Read(ada));

        // ISO, so the value stored on the request and the value applied are the same
        // string whatever the viewer's locale.
        Assert.Equal("1990-04-12", ProfileFields.Find("dateOfBirth")!.Read(ada));
    }

    [Fact]
    public void AnUnsetFieldReadsAsNull() =>
        Assert.Null(ProfileFields.Find("preferredName")!.Read(Ada()));

    // ── applying ────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyingWritesTheField()
    {
        EmployeeProfile ada = Ada();

        Assert.Equal(ProfileFieldOutcome.Applied,
            ProfileFields.Find("preferredName")!.Apply(ada, "Addie"));

        Assert.Equal("Addie", ada.Employee.PreferredName);
    }

    [Fact]
    public void ApplyingADateParsesIt()
    {
        EmployeeProfile ada = Ada();

        Assert.Equal(ProfileFieldOutcome.Applied,
            ProfileFields.Find("dateOfBirth")!.Apply(ada, "1991-05-03"));

        Assert.Equal(new DateOnly(1991, 5, 3), ada.Employee.DateOfBirth);
    }

    // Refused rather than stored as a string nobody can read back as a date. The
    // approval is the moment this is caught, because that is the moment it would
    // otherwise reach the record.
    [Fact]
    public void ApplyingANonsenseDateIsRefused()
    {
        EmployeeProfile ada = Ada();
        DateOnly before = ada.Employee.DateOfBirth!.Value;

        Assert.Equal(ProfileFieldOutcome.NotADate,
            ProfileFields.Find("dateOfBirth")!.Apply(ada, "next Tuesday"));

        Assert.Equal(before, ada.Employee.DateOfBirth);
    }

    // A date of birth in the future is somebody mistyping the year, and it would
    // quietly break every age and long-service figure that reads it.
    [Fact]
    public void ADateOfBirthInTheFutureIsRefused()
    {
        EmployeeProfile ada = Ada();

        Assert.Equal(ProfileFieldOutcome.OutOfRange,
            ProfileFields.Find("dateOfBirth")!.Apply(
                ada, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1).ToString("yyyy-MM-dd")));
    }

    // Clearing an optional field is a legitimate request: somebody's preferred name
    // or second phone number can stop being true.
    [Fact]
    public void ClearingAnOptionalFieldIsAllowed()
    {
        EmployeeProfile ada = Ada();

        Assert.Equal(ProfileFieldOutcome.Applied,
            ProfileFields.Find("phone")!.Apply(ada, null));

        Assert.Null(ada.Employee.Phone);
    }

    // A required field cannot be cleared. An employee with no surname breaks every
    // list, every letter and every payroll export that reads one.
    [Fact]
    public void ClearingARequiredFieldIsRefused()
    {
        EmployeeProfile ada = Ada();

        Assert.Equal(ProfileFieldOutcome.Required,
            ProfileFields.Find("lastName")!.Apply(ada, "   "));

        Assert.Equal("Okafor", ada.Employee.LastName);
    }

    [Fact]
    public void AValueTooLongForTheColumnIsRefused()
    {
        EmployeeProfile ada = Ada();

        Assert.Equal(ProfileFieldOutcome.TooLong,
            ProfileFields.Find("firstName")!.Apply(ada, new string('a', 500)));

        Assert.Equal("Ada", ada.Employee.FirstName);
    }

    // Trimmed on the way in, so "Ada " and "Ada" are not two different requests and
    // the stored value is the one somebody meant.
    [Fact]
    public void AppliedValuesAreTrimmed()
    {
        EmployeeProfile ada = Ada();

        Assert.Equal(ProfileFieldOutcome.Applied,
            ProfileFields.Find("firstName")!.Apply(ada, "  Adaeze  "));

        Assert.Equal("Adaeze", ada.Employee.FirstName);
    }

    // FullName is computed from the names, so a rename leaves it agreeing on its own.
    // Initials is a stored column somebody may have set by hand, and is deliberately
    // left alone — see the note at the foot of ProfileFields.
    [Fact]
    public void RenamingLeavesTheDerivedNameConsistent()
    {
        EmployeeProfile ada = Ada();

        ProfileFields.Find("firstName")!.Apply(ada, "Chioma");

        Assert.Equal("Chioma Okafor", ada.Employee.FullName);
    }
}
