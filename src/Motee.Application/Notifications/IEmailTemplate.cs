namespace Motee.Application.Notifications;

// One implementation per kind of email. Adding a new kind is a new model record and a
// new template class - never an edit to a business service, which is where the wording
// used to live and why OtpService knew how to phrase a sentence.
//
// TModel is contravariant so a template can accept a base model if several kinds ever
// share one shape.
public interface IEmailTemplate<in TModel>
    where TModel : notnull
{
    EmailContent Render(TModel model);
}

// What a template produces: everything except the recipient, which is the dispatcher's
// job. Templates decide wording; they do not decide who gets it.
public sealed record EmailContent
{
    public required string Subject { get; init; }

    public required string TextBody { get; init; }

    // The body only, without the surrounding chrome. The layout is applied once by the
    // dispatcher so every email shares it and no template can forget it.
    public required string HtmlBody { get; init; }
}
