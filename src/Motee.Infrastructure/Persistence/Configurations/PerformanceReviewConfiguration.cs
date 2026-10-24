using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Performance;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class PerformanceReviewConfiguration : IEntityTypeConfiguration<PerformanceReview>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<PerformanceReview> builder)
    {
        builder.ToTable("performance_reviews");

        builder.HasKey(review => review.Id);

        builder.Property(review => review.Period).HasMaxLength(100).IsRequired();

        builder.Property(review => review.ReviewType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(review => review.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(review => review.Strengths).HasMaxLength(4000);
        builder.Property(review => review.Improvements).HasMaxLength(4000);
        builder.Property(review => review.Comments).HasMaxLength(4000);

        // jsonb rather than four columns. The four fields are written together,
        // read together and never queried on — nobody filters reviews by what
        // somebody wrote under "challenges" — and keeping them as one value means
        // adding a fifth prompt is not a migration.
        builder.Property(review => review.SelfAssessment)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<SelfAssessment>(json, Json),
                new ValueComparer<SelfAssessment?>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<SelfAssessment>(
                        JsonSerializer.Serialize(value, Json), Json)));

        // Somebody's own reviews: every read on the employee screen.
        builder.HasIndex(review => new { review.TenantId, review.EmployeeId });

        // A manager's queue — the reviews waiting on them.
        builder.HasIndex(review => new { review.TenantId, review.ReviewerId });

        // The overdue filter, which is a date comparison rather than a status.
        builder.HasIndex(review => new { review.TenantId, review.DueDate });
    }
}
