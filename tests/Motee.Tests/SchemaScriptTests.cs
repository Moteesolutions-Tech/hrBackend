using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Motee.Domain.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests;

// The schema is applied by hand, not by EF migrations. This emits the DDL for the
// current model so it can be run against a fresh database, and fails if the model
// ever drifts from snake_case naming.
file sealed class NoTenant : Motee.Application.Tenancy.ICurrentTenant
{
    public Guid? TenantId => null;
}

public partial class SchemaScriptTests
{
    private const string OutputPath = "schema.generated.sql";

    private static MoteeDbContext BuildContext()
    {
        DbContextOptions<MoteeDbContext> options = new DbContextOptionsBuilder<MoteeDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new MoteeDbContext(options, new NoTenant());
    }

    [Fact]
    public void EmitCreateScript()
    {
        using MoteeDbContext context = BuildContext();

        // No reference data any more. Access levels are seeded per tenant at
        // registration, not shipped with the schema, so the script is pure DDL.
        string script = Idempotent(context.Database.GenerateCreateScript());

        File.WriteAllText(OutputPath, script);

        Assert.Contains("CREATE TABLE IF NOT EXISTS tenants", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS users", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS access_levels", script, StringComparison.Ordinal);

        // Identity's role tables are gone with the roles themselves.
        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS roles", script, StringComparison.Ordinal);

        // Every statement has to be re-runnable, or applying the schema twice leaves
        // a half-migrated database — which is what happens in practice, because
        // nobody knows whether it was applied already.
        Assert.DoesNotMatch(Unguarded, script);
    }

    // Anchored at the start of a line, so the first statement in the file is caught
    // too — it has no newline in front of it.
    [GeneratedRegex(@"^CREATE (TABLE|(UNIQUE )?INDEX) (?!IF NOT EXISTS)", RegexOptions.Multiline)]
    private static partial Regex UnguardedCreate();

    private static Regex Unguarded => UnguardedCreate();

    // EF emits bare CREATE statements. Foreign keys and primary keys are inline in
    // the CREATE TABLE, so guarding tables and indexes covers the whole script.
    private static string Idempotent(string script) =>
        UnguardedCreate().Replace(script, match => $"{match.Value}IF NOT EXISTS ");

    // Postgres folds unquoted identifiers to lower case, so anything that survives
    // in double quotes is a name the model failed to snake_case — including raw SQL
    // such as index filters, which no convention rewrites.
    [Fact]
    public void ScriptContainsNoQuotedIdentifiers()
    {
        using MoteeDbContext context = BuildContext();

        string script = context.Database.GenerateCreateScript();

        List<string> quoted = System.Text.RegularExpressions.Regex
            .Matches(script, "\"[^\"]+\"")
            .Select(match => match.Value)
            .Distinct()
            .ToList();

        Assert.True(quoted.Count == 0, "Quoted identifiers left in DDL: " + string.Join(", ", quoted));
    }

    [Fact]
    public void EveryTableAndColumnIsSnakeCase()
    {
        using MoteeDbContext context = BuildContext();

        List<string> offenders = [];

        foreach (Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType in context.Model.GetEntityTypes())
        {
            string? table = entityType.GetTableName();

            if (table is not null && !IsSnakeCase(table))
            {
                offenders.Add($"table {table}");
            }

            foreach (Microsoft.EntityFrameworkCore.Metadata.IProperty property in entityType.GetProperties())
            {
                string column = property.GetColumnName();

                if (!IsSnakeCase(column))
                {
                    offenders.Add($"column {table}.{column}");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    private static bool IsSnakeCase(string value) =>
        value.All(character => char.IsLower(character) || char.IsDigit(character) || character == '_');
}
