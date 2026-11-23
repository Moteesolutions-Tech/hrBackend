using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Eor;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class EorProviderConfiguration : IEntityTypeConfiguration<EorProvider>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // Compared by serialising, like every other jsonb column here — which is
    // why the service replaces the list rather than adding to it: a mutation in
    // place leaves the comparer seeing no change and the save does nothing.
    private static readonly ValueComparer<List<string>> Comparer =
        new(
            (left, right) => JsonSerializer.Serialize(left, Options)
                == JsonSerializer.Serialize(right, Options),
            value => JsonSerializer.Serialize(value, Options).GetHashCode(),
            value => JsonSerializer.Deserialize<List<string>>(
                JsonSerializer.Serialize(value, Options), Options)!);

    public void Configure(EntityTypeBuilder<EorProvider> builder)
    {
        builder.ToTable("eor_providers");

        builder.HasKey(provider => provider.Id);

        builder.Property(provider => provider.Name).HasMaxLength(200).IsRequired();
        builder.Property(provider => provider.Website).HasMaxLength(2000);

        builder.Property(provider => provider.Status)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(provider => provider.ManagementFeePercent).HasPrecision(5, 2);

        builder.Property(provider => provider.CountriesCovered)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Options),
                json => JsonSerializer.Deserialize<List<string>>(json, Options)!,
                Comparer)
            .IsRequired();

        builder.HasIndex(provider => new { provider.TenantId, provider.Status });
    }
}

internal sealed class EorEngagementConfiguration : IEntityTypeConfiguration<EorEngagement>
{
    public void Configure(EntityTypeBuilder<EorEngagement> builder)
    {
        builder.ToTable("eor_engagements");

        builder.HasKey(engagement => engagement.Id);

        builder.Property(engagement => engagement.CountryOfEngagement)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(engagement => engagement.Currency).HasMaxLength(3).IsRequired();

        builder.Property(engagement => engagement.GrossMonthly).HasPrecision(14, 2);
        builder.Property(engagement => engagement.EmployerCostMonthly).HasPrecision(14, 2);
        builder.Property(engagement => engagement.UsdRate).HasPrecision(18, 8);

        builder.Property(engagement => engagement.PayFrequency)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        // Restricted rather than cascaded: deleting a provider must not quietly
        // delete the record of who was employed through them.
        builder.HasOne<EorProvider>()
            .WithMany()
            .HasForeignKey(engagement => engagement.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(engagement => new { engagement.TenantId, engagement.ProviderId });
        builder.HasIndex(engagement =>
            new { engagement.TenantId, engagement.StartDate, engagement.EndDate });
    }
}

internal sealed class EorComplianceItemConfiguration : IEntityTypeConfiguration<EorComplianceItem>
{
    public void Configure(EntityTypeBuilder<EorComplianceItem> builder)
    {
        builder.ToTable("eor_compliance_items");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Key)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(item => item.State)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(item => item.Note).HasMaxLength(2000);

        builder.HasOne<EorEngagement>()
            .WithMany()
            .HasForeignKey(item => item.EngagementId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(item => new { item.EngagementId, item.Key }).IsUnique();
        builder.HasIndex(item => new { item.TenantId, item.ExpiresOn });
    }
}

internal sealed class EorInvoiceConfiguration : IEntityTypeConfiguration<EorInvoice>
{
    public void Configure(EntityTypeBuilder<EorInvoice> builder)
    {
        builder.ToTable("eor_invoices");

        builder.HasKey(invoice => invoice.Id);

        builder.Property(invoice => invoice.Currency).HasMaxLength(3).IsRequired();
        builder.Property(invoice => invoice.Reference).HasMaxLength(100);

        foreach (string property in (string[])
        [
            nameof(EorInvoice.GrossPay),
            nameof(EorInvoice.EmployerCosts),
            nameof(EorInvoice.ManagementFee),
            nameof(EorInvoice.Total),
        ])
        {
            builder.Property(property).HasPrecision(14, 2);
        }

        builder.HasOne<EorProvider>()
            .WithMany()
            .HasForeignKey(invoice => invoice.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(invoice => new { invoice.ProviderId, invoice.Period }).IsUnique();
        builder.HasIndex(invoice => new { invoice.TenantId, invoice.DueOn });
    }
}
