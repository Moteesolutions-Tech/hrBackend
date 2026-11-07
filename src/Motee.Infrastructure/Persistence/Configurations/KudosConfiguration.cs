using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Recognition;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class KudosPostConfiguration : IEntityTypeConfiguration<KudosPost>
{
    public void Configure(EntityTypeBuilder<KudosPost> builder)
    {
        builder.ToTable("kudos_posts");

        builder.HasKey(post => post.Id);

        builder.Property(post => post.Message).HasMaxLength(500).IsRequired();
        builder.Property(post => post.CustomTypeName).HasMaxLength(60);

        foreach (string property in (string[])
        [
            nameof(KudosPost.KudosType),
            nameof(KudosPost.CompanyValue),
            nameof(KudosPost.Visibility),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(20).IsRequired();
        }

        // The wall, newest first with pinned held at the top.
        builder
            .HasIndex(post => new { post.TenantId, post.IsPinned, post.CreatedAt })
            .HasDatabaseName("ix_kudos_posts_wall");

        builder
            .HasIndex(post => new { post.TenantId, post.RecipientEmployeeId })
            .HasDatabaseName("ix_kudos_posts_tenant_id_recipient_employee_id");

        builder
            .HasIndex(post => new { post.TenantId, post.SenderEmployeeId })
            .HasDatabaseName("ix_kudos_posts_tenant_id_sender_employee_id");
    }
}

internal sealed class KudosReactionConfiguration : IEntityTypeConfiguration<KudosReaction>
{
    public void Configure(EntityTypeBuilder<KudosReaction> builder)
    {
        builder.ToTable("kudos_reactions");

        builder.HasKey(reaction => reaction.Id);

        builder.Property(reaction => reaction.Type)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Declared so EF orders the inserts, and so removing a post takes its
        // reactions with it.
        builder
            .HasOne<KudosPost>()
            .WithMany()
            .HasForeignKey(reaction => reaction.KudosPostId)
            .OnDelete(DeleteBehavior.Cascade);

        // One reaction of each kind per person per post. The count is read
        // from these rows, so a duplicate would be a wrong number on screen
        // with nothing to point at.
        builder
            .HasIndex(reaction => new
            {
                reaction.KudosPostId,
                reaction.EmployeeId,
                reaction.Type,
            })
            .IsUnique()
            .HasDatabaseName("ix_kudos_reactions_one_per_person_per_type");
    }
}

internal sealed class KudosCommentConfiguration : IEntityTypeConfiguration<KudosComment>
{
    public void Configure(EntityTypeBuilder<KudosComment> builder)
    {
        builder.ToTable("kudos_comments");

        builder.HasKey(comment => comment.Id);

        builder.Property(comment => comment.Message).HasMaxLength(1000).IsRequired();

        builder
            .HasOne<KudosPost>()
            .WithMany()
            .HasForeignKey(comment => comment.KudosPostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(comment => new { comment.KudosPostId, comment.CreatedAt })
            .HasDatabaseName("ix_kudos_comments_post_created_at");
    }
}
