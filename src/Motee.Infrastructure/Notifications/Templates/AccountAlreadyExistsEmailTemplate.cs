using Motee.Application.Notifications;

namespace Motee.Infrastructure.Notifications.Templates;

internal sealed class AccountAlreadyExistsEmailTemplate : IEmailTemplate<AccountAlreadyExistsEmail>
{
    public EmailContent Render(AccountAlreadyExistsEmail model) => new()
    {
        // Deliberately not "welcome" or "verify" - the subject is the first thing the
        // recipient sees, and it has to distinguish this from the code they were
        // expecting, or they will sit waiting for a code that is never coming.
        Subject = "You already have a Motee account",

        TextBody =
            "Someone just tried to create a Motee account with this email address, but "
            + "you already have one.\n\n"
            + $"Sign in: {model.SignInUrl}\n"
            + $"Forgotten your password: {model.ForgotPasswordUrl}\n\n"
            + "If that was not you, no action is needed - no account was created and "
            + "nothing about yours has changed. Someone may simply have mistyped their "
            + "own address.",

        HtmlBody =
            EmailLayout.Paragraph(
                "Someone just tried to create a Motee account with this email address, "
                + "but you already have one.")
            + EmailLayout.Button(model.SignInUrl, "Sign in")
            + EmailLayout.Note($"Forgotten your password: {model.ForgotPasswordUrl}")
            + EmailLayout.Note(
                "If that was not you, no action is needed - no account was created and "
                + "nothing about yours has changed. Someone may simply have mistyped "
                + "their own address."),
    };
}
