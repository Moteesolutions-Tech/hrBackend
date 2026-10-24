using Microsoft.EntityFrameworkCore;
using Motee.Application.Notifications;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Notifications;

internal sealed class EmailDeliveryLog(
    MoteeDbContext dbContext,
    TimeProvider timeProvider) : IEmailDeliveryLog
{
    public async Task MarkSentAsync(
        EmailAbout about,
        CancellationToken cancellationToken = default)
    {
        if (about.Kind != EmailAbout.Invitation)
        {
            return;
        }

        // IgnoreQueryFilters because this runs on a background worker, which belongs to
        // no tenant. Scoped by primary key instead, which is narrower than the filter
        // would have been: an id names one row or none.
        await dbContext.EmployeeInvitations
            .IgnoreQueryFilters()
            .Where(invitation => invitation.Id == about.Id && invitation.EmailSentAt == null)
            .ExecuteUpdateAsync(
                row => row.SetProperty(
                    invitation => invitation.EmailSentAt, timeProvider.GetUtcNow()),
                cancellationToken);
    }
}
