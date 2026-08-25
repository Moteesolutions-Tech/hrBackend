namespace Motee.Application.Notifications;

// Hands a rendered email to background delivery. Callers do not wait for the send, so a
// slow or failing provider never blocks a sign-up response.
public interface IEmailQueue
{
    void Enqueue(EmailMessage message);
}
