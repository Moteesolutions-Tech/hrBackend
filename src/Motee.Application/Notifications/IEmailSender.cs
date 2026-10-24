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

    // What this email is about, so its delivery can be written back against that
    // record. Opaque to the mail layer, which carries it and hands it back without
    // knowing what it names.
    //
    // It has to ride on the message rather than be looked up later: an invitation's
    // raw token exists once, in this email, and only its hash is stored — so nothing
    // downstream can rebuild the link from the record.
    //
    // Null for most emails. A verification code that fails is already alerted and
    // already has a Resend button; an invitation that fails is somebody waiting for a
    // link who will wait for ever, and their company cannot see our alerts.
    public EmailAbout? About { get; init; }
}

// Which record an email belongs to. A pair rather than a formatted string, so a typo
// cannot quietly match nothing. Plain, because it is serialised into a Hangfire job.
public sealed record EmailAbout
{
    public const string Invitation = "invitation";

    public required string Kind { get; init; }

    public required Guid Id { get; init; }
}
