using Motee.Application.Notifications;
using Motee.Domain.Auth;

namespace Motee.Infrastructure.Notifications.Templates;

internal sealed class OtpCodeEmailTemplate : IEmailTemplate<OtpCodeEmail>
{
    public EmailContent Render(OtpCodeEmail model)
    {
        string minutes = $"{model.Lifetime.TotalMinutes:0}";

        string action = model.Purpose switch
        {
            OtpPurpose.EmailVerification => "confirm your email address",
            OtpPurpose.PasswordReset => "reset your password",
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };

        return new EmailContent
        {
            Subject = model.Purpose switch
            {
                OtpPurpose.EmailVerification => "Verify your Motee account",
                OtpPurpose.PasswordReset => "Reset your Motee password",
                _ => throw new ArgumentOutOfRangeException(nameof(model)),
            },

            TextBody =
                $"Your Motee verification code is {model.Code}.\n\n"
                + $"Use it to {action}. It expires in {minutes} minutes.\n\n"
                + "If you did not request this, ignore this message and the code "
                + "expires on its own.",

            HtmlBody =
                EmailLayout.Paragraph($"Use this code to {action}.")
                + EmailLayout.CodeBlock(model.Code)
                + EmailLayout.Note($"The code expires in {minutes} minutes.")
                + EmailLayout.Note(
                    "If you did not request this, ignore this message - the code "
                    + "expires on its own and nothing changes."),
        };
    }
}
