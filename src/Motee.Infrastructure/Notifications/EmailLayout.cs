using System.Net;
using System.Text;

namespace Motee.Infrastructure.Notifications;

// The chrome every email shares. Applied by the dispatcher rather than by each template,
// so a new email cannot ship without it and a branding change is one edit.
//
// Written as inline-styled tables on purpose. Email clients are not browsers: Outlook
// renders with Word, Gmail strips <style> blocks, and flexbox is not available anywhere
// worth relying on. This looks like 2005 because that is what survives the trip.
internal static class EmailLayout
{
    private const string Ink = "#1f2933";
    private const string Muted = "#6b7280";
    private const string Rule = "#e5e7eb";
    private const string Page = "#f5f6f8";

    public static string Wrap(string title, string bodyHtml)
    {
        StringBuilder html = new();

        html.Append(
            $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{Escape(title)}</title>
            </head>
            <body style="margin:0;padding:0;background:{Page};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                   style="background:{Page};padding:24px 12px;">
              <tr><td align="center">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                       style="max-width:560px;background:#ffffff;border:1px solid {Rule};
                              border-radius:12px;padding:32px;
                              font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',
                              Roboto,Helvetica,Arial,sans-serif;color:{Ink};
                              font-size:16px;line-height:1.55;">
                  <tr><td style="padding-bottom:24px;font-weight:700;font-size:20px;
                                 letter-spacing:-0.01em;">Motee</td></tr>
                  <tr><td>
            """);

        html.Append(bodyHtml);

        html.Append(
            $"""
                  </td></tr>
                  <tr><td style="padding-top:28px;border-top:1px solid {Rule};
                                 margin-top:28px;color:{Muted};font-size:13px;
                                 line-height:1.5;">
                    This message was sent by Motee. If you were not expecting it, you can
                    ignore it safely - nothing happens unless you act on it.
                  </td></tr>
                </table>
              </td></tr>
            </table>
            </body>
            </html>
            """);

        return html.ToString();
    }

    // Anything interpolated into HTML goes through here. Names and company names reach
    // these templates from user input, and an unescaped apostrophe in a company name is
    // the cheap version of the same bug that becomes script injection.
    public static string Escape(string value) => WebUtility.HtmlEncode(value);

    public static string Paragraph(string text) =>
        $"""<p style="margin:0 0 16px;">{Escape(text)}</p>""";

    // A code shown big enough to read off a phone and copy without selecting whitespace.
    public static string CodeBlock(string code) =>
        $"""
        <p style="margin:0 0 20px;padding:16px 20px;background:{Page};
                  border:1px solid {Rule};border-radius:8px;font-size:28px;
                  font-weight:700;letter-spacing:0.18em;text-align:center;
                  font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;">
          {Escape(code)}
        </p>
        """;

    // The URL is also shown as text beneath, because a button that fails to render
    // leaves the reader with nothing to act on.
    public static string Button(string url, string label) =>
        $"""
        <p style="margin:0 0 16px;">
          <a href="{Escape(url)}"
             style="display:inline-block;padding:12px 22px;background:{Ink};
                    color:#ffffff;text-decoration:none;border-radius:8px;
                    font-weight:600;font-size:15px;">{Escape(label)}</a>
        </p>
        <p style="margin:0 0 16px;color:{Muted};font-size:13px;word-break:break-all;">
          {Escape(url)}
        </p>
        """;

    public static string Note(string text) =>
        $"""<p style="margin:0 0 16px;color:{Muted};font-size:14px;">{Escape(text)}</p>""";
}
