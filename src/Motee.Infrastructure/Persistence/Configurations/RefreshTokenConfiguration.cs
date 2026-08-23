using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Auth;
using Motee.Infrastructure.Identity;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(token => token.Id);

        builder.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(token => token.CreatedByIp).HasMaxLength(64);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Every refresh is a lookup by hash, so it carries the unique index.
        builder.HasIndex(token => token.TokenHash)
            .HasDatabaseName("ix_refresh_tokens_hash")
            .IsUnique();

        builder.HasIndex(token => token.UserId).HasDatabaseName("ix_refresh_tokens_user");
    }
}
