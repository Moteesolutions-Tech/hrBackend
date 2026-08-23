using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Auth;
using Motee.Infrastructure.Identity;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class OtpChallengeConfiguration : IEntityTypeConfiguration<OtpChallengeRecord>
{
    public void Configure(EntityTypeBuilder<OtpChallengeRecord> builder)
    {
        builder.ToTable("otp_challenges");

        builder.HasKey(challenge => challenge.Id);

        // Stored as text so the column reads plainly in SQL and survives enum reordering.
        builder.Property(challenge => challenge.Purpose)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(challenge => challenge.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Verification looks up the live challenge for a user and purpose.
        builder.HasIndex(challenge => new { challenge.UserId, challenge.Purpose })
            .HasDatabaseName("ix_otp_challenges_user_purpose");

        // Supports sweeping expired rows.
        builder.HasIndex(challenge => challenge.IssuedAt)
            .HasDatabaseName("ix_otp_challenges_issued_at");
    }
}
