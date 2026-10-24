using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Performance;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class PerformanceFeedbackConfiguration
    : IEntityTypeConfiguration<PerformanceFeedback>
{
    public void Configure(EntityTypeBuilder<PerformanceFeedback> builder)
    {
        builder.ToTable("performance_feedback");

        builder.HasKey(feedback => feedback.Id);

        builder.Property(feedback => feedback.Type)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(feedback => feedback.Message).HasMaxLength(4000).IsRequired();

        // Everything written about one person: what a review is read alongside.
        builder.HasIndex(feedback =>
            new { feedback.TenantId, feedback.AboutEmployeeId });
    }
}

internal sealed class FeedbackRequestConfiguration : IEntityTypeConfiguration<FeedbackRequest>
{
    public void Configure(EntityTypeBuilder<FeedbackRequest> builder)
    {
        builder.ToTable("feedback_requests");

        builder.HasKey(request => request.Id);

        builder.Property(request => request.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(request => request.Context).HasMaxLength(1000);

        // One open ask per colleague. A second is a nudge rather than a new
        // question, and two in somebody's list look like two different ones.
        //
        // Partial, so asking the same colleague again next year is fine — it is
        // only the open ones that have to be unique.
        builder.HasIndex(request =>
                new { request.TenantId, request.AskedByEmployeeId, request.AskedOfEmployeeId })
            .IsUnique()
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ix_feedback_requests_one_open_ask");

        // A colleague's queue of things to answer.
        builder.HasIndex(request =>
            new { request.TenantId, request.AskedOfEmployeeId, request.Status });
    }
}
