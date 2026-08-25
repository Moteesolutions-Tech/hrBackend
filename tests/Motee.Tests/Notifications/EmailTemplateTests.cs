using Motee.Application.Notifications;
using Motee.Domain.Auth;
using Motee.Domain.Employees;
using Motee.Infrastructure.Notifications;
using Motee.Infrastructure.Notifications.Templates;

namespace Motee.Tests.Notifications;

// Rendering is separable from sending, which is the point of the template split - the
// wording used to be built inside OtpService and could only be observed by running the
// whole flow against a database.
public class EmailTemplateTests
{
    private static EmailContent Otp(OtpPurpose purpose) =>
        new OtpCodeEmailTemplate().Render(new OtpCodeEmail
        {
            Code = "482913",
            Lifetime = TimeSpan.FromMinutes(10),
            Purpose = purpose,
        });

    [Fact]
    public void TheOtpCodeAppearsInBothBodies()
    {
        EmailContent content = Otp(OtpPurpose.EmailVerification);

        Assert.Contains("482913", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("482913", content.HtmlBody, StringComparison.Ordinal);
    }

    // The same code authorises two very different things. Telling someone their password
    // is being reset when they asked to confirm an address is how people are trained to
    // ignore the warning that matters.
    [Fact]
    public void TheOtpSubjectSaysWhichActionItAuthorises()
    {
        Assert.Equal("Verify your Motee account", Otp(OtpPurpose.EmailVerification).Subject);
        Assert.Equal("Reset your Motee password", Otp(OtpPurpose.PasswordReset).Subject);
    }

    [Fact]
    public void TheOtpLifetimeIsStated()
    {
        Assert.Contains("10 minutes", Otp(OtpPurpose.EmailVerification).TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportLinkAppearsAndIsDescribedAsSensitive()
    {
        EmailContent content = new ExportReadyEmailTemplate().Render(new ExportReadyEmail
        {
            RowCount = 1234,
            DownloadUrl = "https://storage.test/export.csv?sig=abc",
            LinkLifetime = TimeSpan.FromHours(24),
            AvailableUntil = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        });

        Assert.Contains("https://storage.test/export.csv?sig=abc", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("1,234", content.TextBody, StringComparison.Ordinal);

        // The link bypasses the app entirely, so the mail has to say so.
        Assert.Contains("forwarded", content.TextBody, StringComparison.OrdinalIgnoreCase);
    }

    // The live value is one hour, because a presigned URL cannot outlive the temporary
    // credentials that signed it. "1 hours" is what that change produces if nobody looks.
    [Theory]
    [InlineData(1, "1 hour")]
    [InlineData(24, "24 hours")]
    public void TheLinkLifetimeReadsAsEnglish(int hours, string expected)
    {
        EmailContent content = new ExportReadyEmailTemplate().Render(new ExportReadyEmail
        {
            RowCount = 1,
            DownloadUrl = "https://storage.test/export.csv",
            LinkLifetime = TimeSpan.FromHours(hours),
            AvailableUntil = DateTimeOffset.UtcNow,
        });

        Assert.Contains($"works for {expected}", content.TextBody, StringComparison.Ordinal);
    }

    private static EmailContent Invite(InvitationPurpose purpose) =>
        new EmployeeInviteEmailTemplate().Render(new EmployeeInviteEmail
        {
            JoinUrl = "https://app.test/join/tok",
            ExpiresAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            Purpose = purpose,
        });

    // Credentials means HR already filled everything in. Telling that person to
    // "complete your onboarding" sends them looking for a form that is not there.
    [Fact]
    public void TheInviteWordingMatchesWhetherThereIsAnythingToFillIn()
    {
        EmailContent credentials = Invite(InvitationPurpose.Credentials);
        EmailContent onboarding = Invite(InvitationPurpose.Onboarding);

        Assert.Equal("Set up your Motee password", credentials.Subject);
        Assert.Contains("Choose a password", credentials.TextBody, StringComparison.Ordinal);

        Assert.Equal("You have been invited to Motee", onboarding.Subject);
        Assert.Contains("onboarding", onboarding.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJoinLinkAppearsInBothBodies()
    {
        EmailContent content = Invite(InvitationPurpose.Onboarding);

        Assert.Contains("https://app.test/join/tok", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://app.test/join/tok", content.HtmlBody, StringComparison.Ordinal);
    }

    // Values reaching a template come from user input - company names, file names. An
    // unescaped one is a broken layout at best and injected markup at worst.
    [Fact]
    public void InterpolatedValuesAreHtmlEscaped()
    {
        EmailContent content = new ExportReadyEmailTemplate().Render(new ExportReadyEmail
        {
            RowCount = 1,
            DownloadUrl = "https://storage.test/a.csv?x=1&y=<script>",
            LinkLifetime = TimeSpan.FromHours(1),
            AvailableUntil = DateTimeOffset.UtcNow,
        });

        Assert.DoesNotContain("<script>", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", content.HtmlBody, StringComparison.Ordinal);
    }

    // Every email gets the same chrome because the dispatcher applies it, not the
    // template - so no new email can ship without it.
    [Fact]
    public void TheLayoutWrapsBodyHtmlInACompleteDocument()
    {
        string wrapped = EmailLayout.Wrap("A subject", "<p>Body</p>");

        Assert.StartsWith("<!doctype html>", wrapped, StringComparison.Ordinal);
        Assert.Contains("<p>Body</p>", wrapped, StringComparison.Ordinal);
        Assert.Contains("Motee", wrapped, StringComparison.Ordinal);
        Assert.EndsWith("</html>", wrapped.TrimEnd(), StringComparison.Ordinal);
    }
}
