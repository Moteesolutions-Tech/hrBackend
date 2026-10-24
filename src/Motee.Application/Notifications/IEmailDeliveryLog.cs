namespace Motee.Application.Notifications;

// Writes back that an email reached the provider.
//
// Two states, not three. A failed send is retried, and nothing can tell from inside an
// attempt whether it is the last one — so "failed" would either arrive early and be
// wrong, or never arrive at all. Unsent until proven sent says exactly as much as we
// know, and the thing a person needs to do about it is the same either way: resend.
//
// Reaching the provider is not the same as reaching the person. A hard bounce happens
// after this and would need the provider to tell us; what this rules out is the
// commoner case where nothing was ever handed over.
public interface IEmailDeliveryLog
{
    // No-ops for a reference nothing recognises, including one whose record has since
    // been deleted. A late or duplicated job must not fail over bookkeeping — the mail
    // went out either way, which is the thing that mattered.
    Task MarkSentAsync(EmailAbout about, CancellationToken cancellationToken = default);
}
