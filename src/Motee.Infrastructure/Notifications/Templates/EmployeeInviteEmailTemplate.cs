using Motee.Application.Notifications;
using Motee.Domain.Employees;

namespace Motee.Infrastructure.Notifications.Templates;

// Both invitations are the same act - here is a one-time link, set yourself up - so they
// share a template. What differs is whether the reader is being asked to fill anything
// in, and getting that wrong sends someone looking for a form that is not there.
internal sealed class EmployeeInviteEmailTemplate : IEmailTemplate<EmployeeInviteEmail>
{
    public EmailContent Render(EmployeeInviteEmail model)
    {
        string expires = $"{model.ExpiresAt:d MMMM yyyy}";
        bool credentialsOnly = model.Purpose == InvitationPurpose.Credentials;

        string subject = credentialsOnly
            ? "Set up your Motee password"
            : "You have been invited to Motee";

        string opening = credentialsOnly
            ? "Your employer has set up your Motee account. Choose a password to sign in."
            : "You have been invited to complete your onboarding.";

        string label = credentialsOnly ? "Choose your password" : "Start onboarding";

        return new EmailContent
        {
            Subject = subject,

            TextBody =
                $"{opening}\n\n"
                + $"{model.JoinUrl}\n\n"
                + $"This link works once, and expires on {expires}.",

            HtmlBody =
                EmailLayout.Paragraph(opening)
                + EmailLayout.Button(model.JoinUrl, label)
                + EmailLayout.Note($"This link works once, and expires on {expires}."),
        };
    }
}
