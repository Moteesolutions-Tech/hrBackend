using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Suggestions;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class SuggestionConfiguration : IEntityTypeConfiguration<Suggestion>
{
    public void Configure(EntityTypeBuilder<Suggestion> builder)
    {
        builder.ToTable("suggestions");

        builder.HasKey(suggestion => suggestion.Id);

        builder.Property(suggestion => suggestion.Title).HasMaxLength(100).IsRequired();
        builder.Property(suggestion => suggestion.Description).HasMaxLength(1000).IsRequired();
        builder.Property(suggestion => suggestion.AdminResponse).HasMaxLength(2000);
        builder.Property(suggestion => suggestion.ImplementationNotes).HasMaxLength(2000);

        foreach (string property in (string[])
        [
            nameof(Suggestion.Category),
            nameof(Suggestion.Status),
            nameof(Suggestion.Priority),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(20).IsRequired();
        }

        // The board: featured at the top, newest first.
        builder
            .HasIndex(suggestion => new
            {
                suggestion.TenantId,
                suggestion.IsFeatured,
                suggestion.CreatedAt,
            })
            .HasDatabaseName("ix_suggestions_board");

        builder
            .HasIndex(suggestion => new { suggestion.TenantId, suggestion.Status })
            .HasDatabaseName("ix_suggestions_tenant_id_status");

        // Somebody finding their own submissions, anonymous ones included.
        builder
            .HasIndex(suggestion => new { suggestion.TenantId, suggestion.SubmitterEmployeeId })
            .HasDatabaseName("ix_suggestions_tenant_id_submitter_employee_id");
    }
}

internal sealed class SuggestionVoteConfiguration : IEntityTypeConfiguration<SuggestionVote>
{
    public void Configure(EntityTypeBuilder<SuggestionVote> builder)
    {
        builder.ToTable("suggestion_votes");

        builder.HasKey(vote => vote.Id);

        // Declared so EF orders the inserts, and so removing a suggestion
        // takes its votes with it.
        builder
            .HasOne<Suggestion>()
            .WithMany()
            .HasForeignKey(vote => vote.SuggestionId)
            .OnDelete(DeleteBehavior.Cascade);

        // One vote per person per suggestion. The count on screen is read from
        // these rows, so a duplicate would be a wrong number with nothing to
        // point at — and this index is what makes a double-click safe.
        builder
            .HasIndex(vote => new { vote.SuggestionId, vote.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_suggestion_votes_one_per_person");
    }
}

internal sealed class SuggestionCommentConfiguration : IEntityTypeConfiguration<SuggestionComment>
{
    public void Configure(EntityTypeBuilder<SuggestionComment> builder)
    {
        builder.ToTable("suggestion_comments");

        builder.HasKey(comment => comment.Id);

        builder.Property(comment => comment.Message).HasMaxLength(1000).IsRequired();

        builder
            .HasOne<Suggestion>()
            .WithMany()
            .HasForeignKey(comment => comment.SuggestionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(comment => new { comment.SuggestionId, comment.CreatedAt })
            .HasDatabaseName("ix_suggestion_comments_suggestion_created_at");
    }
}
