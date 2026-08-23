namespace Motee.Application.Auth;

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public sealed record EmailMessage
{
    public required string To { get; init; }

    public required string Subject { get; init; }

    public required string Body { get; init; }
}
