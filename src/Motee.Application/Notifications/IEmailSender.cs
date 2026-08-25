namespace Motee.Application.Notifications;

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

// What actually goes on the wire, after a template has rendered. Deliberately a plain
// record with no behaviour: it is serialised into a Hangfire job, so anything clever
// here becomes a deserialisation problem on the other side.
public sealed record EmailMessage
{
    public required string To { get; init; }

    public required string Subject { get; init; }

    // Always present. Some clients refuse HTML, spam scoring counts a missing text part
    // against you, and it is what a developer reads in the console locally.
    public required string TextBody { get; init; }

    public string? HtmlBody { get; init; }
}
