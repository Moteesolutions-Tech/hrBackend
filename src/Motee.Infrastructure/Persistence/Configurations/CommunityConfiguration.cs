using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Community;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class CommunityPostConfiguration : IEntityTypeConfiguration<CommunityPost>
{
    public void Configure(EntityTypeBuilder<CommunityPost> builder)
    {
        builder.ToTable("community_posts");

        builder.HasKey(post => post.Id);

        builder.Property(post => post.Content).HasMaxLength(4000).IsRequired();
        builder.Property(post => post.EventLocation).HasMaxLength(200);
        builder.Property(post => post.PollQuestion).HasMaxLength(300);
        builder.Property(post => post.CelebrationDetail).HasMaxLength(200);

        builder.Property(post => post.PostType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(post => post.CelebrationKind)
            .HasConversion<string>()
            .HasMaxLength(20);

        // The feed: pinned first, then newest.
        builder
            .HasIndex(post => new { post.TenantId, post.IsPinned, post.CreatedAt })
            .HasDatabaseName("ix_community_posts_feed");
    }
}

internal sealed class PostPollOptionConfiguration : IEntityTypeConfiguration<PostPollOption>
{
    public void Configure(EntityTypeBuilder<PostPollOption> builder)
    {
        builder.ToTable("post_poll_options");

        builder.HasKey(option => option.Id);

        builder.Property(option => option.Label).HasMaxLength(200).IsRequired();

        builder
            .HasOne<CommunityPost>()
            .WithMany()
            .HasForeignKey(option => option.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(option => new { option.PostId, option.Sequence })
            .HasDatabaseName("ix_post_poll_options_post_sequence");
    }
}

internal sealed class PostPollVoteConfiguration : IEntityTypeConfiguration<PostPollVote>
{
    public void Configure(EntityTypeBuilder<PostPollVote> builder)
    {
        builder.ToTable("post_poll_votes");

        builder.HasKey(vote => vote.Id);

        builder
            .HasOne<CommunityPost>()
            .WithMany()
            .HasForeignKey(vote => vote.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        // Declared so the option rows are written before the votes that point
        // at them, and so removing an option takes its votes with it.
        builder
            .HasOne<PostPollOption>()
            .WithMany()
            .HasForeignKey(vote => vote.OptionId)
            .OnDelete(DeleteBehavior.Cascade);

        // One vote per person per poll, not per option. The client keeps a
        // list of voters on each option, which lets the same person appear
        // under two answers and makes the shares add to more than a hundred.
        builder
            .HasIndex(vote => new { vote.PostId, vote.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_post_poll_votes_one_per_person");
    }
}

internal sealed class PostReactionConfiguration : IEntityTypeConfiguration<PostReaction>
{
    public void Configure(EntityTypeBuilder<PostReaction> builder)
    {
        builder.ToTable("post_reactions");

        builder.HasKey(reaction => reaction.Id);

        builder
            .HasOne<CommunityPost>()
            .WithMany()
            .HasForeignKey(reaction => reaction.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        // One like per person per post. The count on screen is read from
        // these rows, so a duplicate would be a wrong number with nothing to
        // point at.
        builder
            .HasIndex(reaction => new { reaction.PostId, reaction.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_post_reactions_one_per_person");
    }
}

internal sealed class PostCommentConfiguration : IEntityTypeConfiguration<PostComment>
{
    public void Configure(EntityTypeBuilder<PostComment> builder)
    {
        builder.ToTable("post_comments");

        builder.HasKey(comment => comment.Id);

        builder.Property(comment => comment.Message).HasMaxLength(2000).IsRequired();

        builder
            .HasOne<CommunityPost>()
            .WithMany()
            .HasForeignKey(comment => comment.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(comment => new { comment.PostId, comment.CreatedAt })
            .HasDatabaseName("ix_post_comments_post_created_at");
    }
}
