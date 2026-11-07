using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Common;
using Motee.Domain.Diversity;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class DiversityDeclarationConfiguration
    : IEntityTypeConfiguration<DiversityDeclaration>
{
    public void Configure(EntityTypeBuilder<DiversityDeclaration> builder)
    {
        builder.ToTable("diversity_declarations");

        builder.HasKey(declaration => declaration.Id);

        // Stored as the option text they chose rather than as a code. The
        // option lists are jurisdictional and change by statute; a code would
        // need a migration to add an ONS category, and a renamed code would
        // silently relabel everybody who had picked it.
        foreach (string property in (string[])
        [
            nameof(DiversityDeclaration.Ethnicity),
            nameof(DiversityDeclaration.Disability),
            nameof(DiversityDeclaration.Religion),
            nameof(DiversityDeclaration.VeteranStatus),
            nameof(DiversityDeclaration.CaringResponsibilities),
        ])
        {
            builder.Property(property).HasMaxLength(80);
        }

        // The ISO alpha-2 string, the same way the tenant's own country
        // persists. CountryCode is a record struct rather than an enum, so it
        // needs the explicit converter — HasConversion<string> on it makes
        // the whole model unmappable.
        builder.Property(declaration => declaration.Jurisdiction)
            .HasConversion(
                code => code.Value,
                value => CountryCode.Parse(value))
            .HasMaxLength(2)
            .IsRequired();

        // One row per person, theirs. Two would be two answers from one
        // employee with nothing to choose between them.
        builder
            .HasIndex(declaration => declaration.EmployeeId)
            .IsUnique()
            .HasDatabaseName("ix_diversity_declarations_one_per_person");
    }
}
