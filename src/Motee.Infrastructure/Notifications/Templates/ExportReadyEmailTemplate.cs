using Motee.Application.Notifications;

namespace Motee.Infrastructure.Notifications.Templates;

internal sealed class ExportReadyEmailTemplate : IEmailTemplate<ExportReadyEmail>
{
    public EmailContent Render(ExportReadyEmail model)
    {
        string hours = $"{model.LinkLifetime.TotalHours:0}";
        string rows = $"{model.RowCount:N0}";
        string until = $"{model.AvailableUntil:d MMMM yyyy}";

        return new EmailContent
        {
            Subject = "Your employee export is ready",

            TextBody =
                $"Your export of {rows} employees is ready.\n\n"
                + $"{model.DownloadUrl}\n\n"
                + $"This link works for {hours} hours. It opens the file directly, so "
                + "treat it like the file itself - anyone it is forwarded to can read "
                + "it.\n\n"
                + $"After that, request the export again. The data stays available "
                + $"until {until}.",

            HtmlBody =
                EmailLayout.Paragraph($"Your export of {rows} employees is ready.")
                + EmailLayout.Button(model.DownloadUrl, "Download the file")
                + EmailLayout.Note(
                    $"This link works for {hours} hours and opens the file directly, so "
                    + "treat it like the file itself - anyone it is forwarded to can "
                    + "read it.")
                + EmailLayout.Note(
                    $"After that, request the export again. The data stays available "
                    + $"until {until}."),
        };
    }
}
