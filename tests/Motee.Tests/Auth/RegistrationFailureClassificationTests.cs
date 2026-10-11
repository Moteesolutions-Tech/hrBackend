using Microsoft.EntityFrameworkCore;
using Motee.Infrastructure.Auth;
using Npgsql;

namespace Motee.Tests.Auth;

// Telling "the caller sent something we already have" apart from "our database is wrong".
//
// This existed as one catch that treated every database failure as the caller's fault and
// returned the innermost exception message as a validation error. A deployment missing a
// column came back to a user as
//
//     400 "42703: column \"attachment_note\" of relation \"approval_templates\" does not exist"
//
// which was wrong three times over: it disclosed the schema, it blamed the user for our
// fault, and because a handled 400 never reaches the exception handler, nothing alerted.
// Every registration on that environment was failing while the system's own view was that
// users kept sending bad input.
public class RegistrationFailureClassificationTests
{
    private static DbUpdateException Wrapping(string sqlState) =>
        new("An error occurred while saving the entity changes.",
            new PostgresException("boom", "ERROR", "ERROR", sqlState));

    // The one database failure registration legitimately races on: the address is checked
    // before saving, so reaching the constraint means two requests arrived together.
    [Fact]
    public void AUniqueViolationIsTheCallersToFix()
    {
        Assert.True(TenantRegistrationService.IsDuplicate(
            Wrapping(PostgresErrorCodes.UniqueViolation)));
    }

    // The regression. A missing column is a broken deployment, and must not be absorbed
    // as a validation error — it has to reach the exception handler so somebody is told.
    [Fact]
    public void AMissingColumnIsNot()
    {
        Assert.False(TenantRegistrationService.IsDuplicate(
            Wrapping(PostgresErrorCodes.UndefinedColumn)));
    }

    // Every other server-side fault lands the same way. None of these is something a
    // caller could have sent differently.
    [Theory]
    [InlineData(PostgresErrorCodes.UndefinedTable)]
    [InlineData(PostgresErrorCodes.UndefinedFunction)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData(PostgresErrorCodes.NotNullViolation)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege)]
    public void OtherDatabaseFaultsAreOurs(string sqlState) =>
        Assert.False(TenantRegistrationService.IsDuplicate(Wrapping(sqlState)));

    // A DbUpdateException with no Postgres cause underneath — a concurrency conflict, or
    // a provider that is not Npgsql in some future test double. Not a duplicate, so it
    // propagates, which is the safe direction.
    [Fact]
    public void ADbFailureWithNoPostgresCauseIsNotTreatedAsADuplicate()
    {
        Assert.False(TenantRegistrationService.IsDuplicate(
            new DbUpdateException("concurrency", new InvalidOperationException("no sqlstate"))));
    }

    // Matched on the SQLSTATE, never on message text: Postgres wordings are localised and
    // change between server versions, so a string match would quietly stop working on an
    // upgrade and start returning raw errors to users again.
    [Fact]
    public void TheMatchDoesNotDependOnMessageText()
    {
        DbUpdateException duplicate = new(
            "nothing about this message mentions a constraint",
            new PostgresException(
                "völlig andere Sprache", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));

        Assert.True(TenantRegistrationService.IsDuplicate(duplicate));
    }
}
