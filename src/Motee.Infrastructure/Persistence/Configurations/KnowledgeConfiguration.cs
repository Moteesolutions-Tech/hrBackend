using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Knowledge;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class KnowledgeArticleConfiguration : IEntityTypeConfiguration<KnowledgeArticle>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<KnowledgeArticle> builder)
    {
        builder.ToTable("knowledge_articles");

        builder.HasKey(article => article.Id);

        builder.Property(article => article.Title).HasMaxLength(200).IsRequired();
        builder.Property(article => article.Summary).HasMaxLength(500);
        builder.Property(article => article.Body).HasMaxLength(10_000).IsRequired();

        builder.Property(article => article.Category)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(article => article.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // jsonb because tags are read with the row and never joined across.
        // The tag filter runs in memory, which is what the service's comment
        // about Postgres is about.
        builder.Property(article => article.Tags)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<List<string>>(json, Json)!,
                new ValueComparer<List<string>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<string>>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();

        // The base: featured first, then newest published.
        builder
            .HasIndex(article => new
            {
                article.TenantId,
                article.Status,
                article.IsFeatured,
            })
            .HasDatabaseName("ix_knowledge_articles_shelf");
    }
}

internal sealed class ArticleReadConfiguration : IEntityTypeConfiguration<ArticleRead>
{
    public void Configure(EntityTypeBuilder<ArticleRead> builder)
    {
        builder.ToTable("article_reads");

        builder.HasKey(read => read.Id);

        // Declared so EF orders the inserts, and so removing an article takes
        // its read rows with it.
        builder
            .HasOne<KnowledgeArticle>()
            .WithMany()
            .HasForeignKey(read => read.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per reader per article. Their own opens are counted on the
        // row, which keeps this bounded by the headcount while still
        // answering both "how many people" and "how often".
        builder
            .HasIndex(read => new { read.ArticleId, read.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_article_reads_one_per_reader");
    }
}

internal sealed class ArticleFeedbackConfiguration : IEntityTypeConfiguration<ArticleFeedback>
{
    public void Configure(EntityTypeBuilder<ArticleFeedback> builder)
    {
        builder.ToTable("article_feedback");

        builder.HasKey(row => row.Id);

        builder.Property(row => row.Vote)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder
            .HasOne<KnowledgeArticle>()
            .WithMany()
            .HasForeignKey(row => row.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        // One answer per person per article. Switching from helpful to not
        // changes the row rather than adding a second vote.
        builder
            .HasIndex(row => new { row.ArticleId, row.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_article_feedback_one_per_person");
    }
}
