using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Expenses;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class ExpenseClaimConfiguration : IEntityTypeConfiguration<ExpenseClaim>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<ExpenseClaim> builder)
    {
        builder.ToTable("expense_claims");

        builder.HasKey(claim => claim.Id);

        builder.Property(claim => claim.Reference).HasMaxLength(20);
        builder.Property(claim => claim.Title).HasMaxLength(200).IsRequired();
        builder.Property(claim => claim.Merchant).HasMaxLength(200).IsRequired();
        builder.Property(claim => claim.Notes).HasMaxLength(2000);
        builder.Property(claim => claim.ReturnedReason).HasMaxLength(1000);

        builder.Property(claim => claim.Currency).HasMaxLength(3).IsRequired();

        // numeric(18,4), never a float. This is money: a double cannot hold 0.10
        // exactly, and the error compounds the moment anything is summed.
        builder.Property(claim => claim.Amount).HasPrecision(18, 4);

        builder.Property(claim => claim.Category)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(claim => claim.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // jsonb. Receipt ids are read with the claim and never queried across —
        // "which claims cite this file" is not a question anybody asks.
        builder.Property(claim => claim.ReceiptFileIds)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<List<Guid>>(json, Json)!,
                new ValueComparer<List<Guid>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<Guid>>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();

        // A reference is quoted in emails and chased by it, so two claims sharing
        // one is worse than none at all. Partial, because drafts have none.
        builder.HasIndex(claim => new { claim.TenantId, claim.Reference })
            .IsUnique()
            .HasFilter("reference IS NOT NULL");

        // Somebody's own claims: every read on the employee screen.
        builder.HasIndex(claim => new { claim.TenantId, claim.EmployeeId });

        // The finance queue.
        builder.HasIndex(claim => new { claim.TenantId, claim.Status });
    }
}
