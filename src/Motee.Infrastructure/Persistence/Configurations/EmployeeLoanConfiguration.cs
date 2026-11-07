using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Loans;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeLoanConfiguration : IEntityTypeConfiguration<EmployeeLoan>
{
    public void Configure(EntityTypeBuilder<EmployeeLoan> builder)
    {
        builder.ToTable("employee_loans");

        builder.HasKey(loan => loan.Id);

        builder.Property(loan => loan.Reference).HasMaxLength(30).IsRequired();
        builder.Property(loan => loan.Currency).HasMaxLength(3).IsRequired();
        builder.Property(loan => loan.Purpose).HasMaxLength(500).IsRequired();

        builder.Property(loan => loan.LoanType)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        // Money, so numeric rather than floating point. Eighteen digits is far
        // more than any staff loan needs and costs nothing.
        builder.Property(loan => loan.Amount).HasPrecision(18, 2).IsRequired();

        builder.Property(loan => loan.InterestRatePercent).HasPrecision(5, 2);

        // Quotable in an email, so it has to be unique per tenant — and the
        // index is what makes two tabs claiming the same number safe.
        builder
            .HasIndex(loan => new { loan.TenantId, loan.Reference })
            .IsUnique()
            .HasDatabaseName("ix_employee_loans_one_per_reference");

        builder
            .HasIndex(loan => new { loan.TenantId, loan.EmployeeId })
            .HasDatabaseName("ix_employee_loans_tenant_id_employee_id");
    }
}

internal sealed class LoanRepaymentConfiguration : IEntityTypeConfiguration<LoanRepayment>
{
    public void Configure(EntityTypeBuilder<LoanRepayment> builder)
    {
        builder.ToTable("loan_repayments");

        builder.HasKey(repayment => repayment.Id);

        builder.Property(repayment => repayment.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(repayment => repayment.Reference).HasMaxLength(100);
        builder.Property(repayment => repayment.Note).HasMaxLength(500);

        builder.Property(repayment => repayment.Method)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Declared so EF orders the inserts: a repayment written before its
        // loan violates the foreign key, which is the lesson the announcement
        // and calendar configurations already carry.
        builder
            .HasOne<EmployeeLoan>()
            .WithMany()
            .HasForeignKey(repayment => repayment.LoanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(repayment => repayment.LoanId)
            .HasDatabaseName("ix_loan_repayments_loan_id");
    }
}
