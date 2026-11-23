using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Helpdesk;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class HelpdeskTicketConfiguration : IEntityTypeConfiguration<HelpdeskTicket>
{
    public void Configure(EntityTypeBuilder<HelpdeskTicket> builder)
    {
        builder.ToTable("helpdesk_tickets");

        builder.HasKey(ticket => ticket.Id);

        builder.Property(ticket => ticket.Reference).HasMaxLength(30).IsRequired();
        builder.Property(ticket => ticket.Subject).HasMaxLength(200).IsRequired();
        builder.Property(ticket => ticket.Description).HasMaxLength(4000).IsRequired();

        foreach (string property in (string[])
        [
            nameof(HelpdeskTicket.Category),
            nameof(HelpdeskTicket.Priority),
            nameof(HelpdeskTicket.Status),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(20).IsRequired();
        }

        // Quoted in an email, so unique per company — and the index is what
        // makes two tabs claiming the same number safe.
        builder
            .HasIndex(ticket => new { ticket.TenantId, ticket.Reference })
            .IsUnique()
            .HasDatabaseName("ix_helpdesk_tickets_one_per_reference");

        // The queue, worst first.
        builder
            .HasIndex(ticket => new { ticket.TenantId, ticket.Status, ticket.Priority })
            .HasDatabaseName("ix_helpdesk_tickets_queue");

        builder
            .HasIndex(ticket => new { ticket.TenantId, ticket.SubmitterEmployeeId })
            .HasDatabaseName("ix_helpdesk_tickets_tenant_id_submitter_employee_id");
    }
}

internal sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
{
    public void Configure(EntityTypeBuilder<TicketMessage> builder)
    {
        builder.ToTable("ticket_messages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Body).HasMaxLength(4000).IsRequired();

        // Declared so EF orders the inserts — a message written before its
        // ticket violates the foreign key — and so deleting a ticket takes its
        // conversation with it.
        builder
            .HasOne<HelpdeskTicket>()
            .WithMany()
            .HasForeignKey(message => message.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(message => new { message.TicketId, message.CreatedAt })
            .HasDatabaseName("ix_ticket_messages_ticket_created_at");
    }
}
