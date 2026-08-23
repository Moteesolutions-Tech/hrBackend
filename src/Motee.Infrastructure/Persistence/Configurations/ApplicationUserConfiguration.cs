using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("users");

        builder.Property(user => user.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(user => user.MiddleName).HasMaxLength(100);
        builder.Property(user => user.LastName).HasMaxLength(100).IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(user => user.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // One address belongs to one company. Identity leaves its email index
        // non-unique and relies on a validator; the constraint is enforced in the
        // database here so a race between two sign-ups cannot slip past it.
        builder.HasIndex(user => user.NormalizedEmail)
            .HasDatabaseName("ix_users_email")
            .IsUnique();

        // Motee signs in by email only, so Identity's separate username is dead
        // weight: two more columns and a second unique index enforcing exactly what
        // ix_users_email already enforces. Unmapped rather than removed from the
        // entity, since UserName lives on IdentityUser itself.
        builder.Ignore(user => user.UserName);
        builder.Ignore(user => user.NormalizedUserName);

        builder.HasIndex(user => user.TenantId)
            .HasDatabaseName("ix_users_tenant");
    }
}
