using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Contracts;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> builder)
    {
        builder.ToTable("contracts");

        builder.HasKey(contract => contract.Id);

        builder.Property(contract => contract.Title).HasMaxLength(250).IsRequired();
        builder.Property(contract => contract.Description).HasMaxLength(4000);
        builder.Property(contract => contract.Currency).HasMaxLength(3).IsRequired();

        builder.Property(contract => contract.ContractType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(contract => contract.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Money, so exact. The same rule as an expense claim.
        builder.Property(contract => contract.Salary).HasPrecision(18, 4);

        builder.HasIndex(contract => new { contract.TenantId, contract.EmployeeId });

        // The expiring list's first pass: everything with an end date, narrowed in
        // SQL before the notice window is applied per contract.
        builder.HasIndex(contract => new { contract.TenantId, contract.EndDate });
    }
}

internal sealed class ContractSignatureConfiguration : IEntityTypeConfiguration<ContractSignature>
{
    public void Configure(EntityTypeBuilder<ContractSignature> builder)
    {
        builder.ToTable("contract_signatures");

        builder.HasKey(signature => signature.Id);

        builder.Property(signature => signature.SignedAs).HasMaxLength(100).IsRequired();

        // Declared without a navigation property, so EF knows to write the contract
        // before anything that points at it.
        builder.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(signature => signature.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(signature => signature.ContractId);
    }
}

internal sealed class ContractNoteConfiguration : IEntityTypeConfiguration<ContractNote>
{
    public void Configure(EntityTypeBuilder<ContractNote> builder)
    {
        builder.ToTable("contract_notes");

        builder.HasKey(note => note.Id);

        builder.Property(note => note.Content).HasMaxLength(4000).IsRequired();

        builder.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(note => note.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(note => note.ContractId);
    }
}
