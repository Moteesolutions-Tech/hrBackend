using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeInvitationConfiguration : IEntityTypeConfiguration<EmployeeInvitation>
{
    public void Configure(EntityTypeBuilder<EmployeeInvitation> builder)
    {
        builder.ToTable("employee_invitations");
        builder.HasKey(invitation => invitation.Id);

        // Hex SHA-256. Unique so the same token can never address two invitations.
        builder.Property(invitation => invitation.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(invitation => invitation.Purpose)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(invitation => invitation.Email).HasMaxLength(320).IsRequired();

        builder.HasIndex(invitation => invitation.TokenHash)
            .HasDatabaseName("ix_employee_invitations_token")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(invitation => invitation.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(invitation => invitation.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(invitation => invitation.EmployeeId)
            .HasDatabaseName("ix_employee_invitations_employee");
    }
}
