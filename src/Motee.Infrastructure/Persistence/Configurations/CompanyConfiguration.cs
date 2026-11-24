using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Company;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class CompanyVerificationConfiguration
    : IEntityTypeConfiguration<CompanyVerification>
{
    public void Configure(EntityTypeBuilder<CompanyVerification> builder)
    {
        builder.ToTable("company_verifications");

        builder.HasKey(verification => verification.Id);

        builder.Property(verification => verification.Kind)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(verification => verification.Stage)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(verification => verification.Number)
            .HasMaxLength(VerificationRules.MaximumNumberLength);

        builder.Property(verification => verification.ReviewedByName).HasMaxLength(200);

        builder.Property(verification => verification.DecisionNote)
            .HasMaxLength(VerificationRules.MaximumNoteLength);

        builder.HasIndex(verification => new { verification.TenantId, verification.Kind })
            .IsUnique();

        builder.HasIndex(verification =>
            new { verification.Stage, verification.SubmittedOn });
    }
}
