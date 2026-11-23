using Motee.Domain.Settings;

namespace Motee.Tests.Settings;

// Naming a device in the trusted-devices list, and reading an account event out
// of the audit trail.
//
// Both exist so neither needs its own table. A trusted-devices table would be a
// list that revokes nothing — the device would carry on using its refresh token
// while the screen showed it gone. A login-activity table would be a second
// copy of the audit trail that can disagree with it, and the auditor's copy is
// the one that matters.
//
// Written before the rules.
public class SessionTests
{
    // ── naming a device ────────────────────────────────────────────────────

    [Fact]
    public void AMacRunningChromeIsNamedAsOne()
    {
        DeviceName device = UserAgents.Describe(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        Assert.Equal("Chrome", device.Browser);
        Assert.Equal("macOS", device.OperatingSystem);
        Assert.Equal("Chrome on macOS", device.Label);
    }

    [Fact]
    public void AniPhoneRunningSafariIsNamedAsOne()
    {
        DeviceName device = UserAgents.Describe(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 "
            + "(KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1");

        Assert.Equal("Safari", device.Browser);
        Assert.Equal("iOS", device.OperatingSystem);
    }

    // Edge and Opera both carry "Chrome" in their user agent, and Chrome
    // carries "Safari". Matching the first thing found would name every
    // browser Chrome and every Chrome Safari.
    [Fact]
    public void EdgeIsNotMistakenForChrome()
    {
        DeviceName device = UserAgents.Describe(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36 Edg/123.0.0.0");

        Assert.Equal("Edge", device.Browser);
        Assert.Equal("Windows", device.OperatingSystem);
    }

    [Fact]
    public void ChromeIsNotMistakenForSafari()
    {
        DeviceName device = UserAgents.Describe(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        Assert.Equal("Chrome", device.Browser);
        Assert.Equal("Linux", device.OperatingSystem);
    }

    [Fact]
    public void FirefoxIsRecognised()
    {
        DeviceName device = UserAgents.Describe(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:125.0) Gecko/20100101 Firefox/125.0");

        Assert.Equal("Firefox", device.Browser);
    }

    // A request with no User-Agent header is a real thing, and guessing is
    // worse than saying so: somebody looking for their own laptop in a list
    // needs the one row that is not theirs to look unfamiliar.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curl/8.4.0")]
    public void SomethingUnrecognisedSaysSo(string? agent)
    {
        Assert.Equal("Unknown device", UserAgents.Describe(agent).Label);
    }

    [Fact]
    public void AKnownSystemWithAnUnknownBrowserStillNamesTheSystem()
    {
        Assert.Equal("Windows", UserAgents.Describe("SomeBot/1.0 (Windows NT 10.0)").Label);
    }

    // ── reading an account event ───────────────────────────────────────────

    [Fact]
    public void ASignInIsASignIn()
    {
        Assert.Equal(
            AccountEventKind.SignIn, AccountEvents.KindOf("Signed in", 200, "Login"));
    }

    // Keyed off the status rather than the wording, so rephrasing the sentence
    // does not turn a failed sign-in into a successful one.
    [Fact]
    public void AFailedSignInIsReadFromTheStatusNotTheWording()
    {
        Assert.Equal(
            AccountEventKind.FailedSignIn,
            AccountEvents.KindOf("Something else entirely", 401, "Login"));
    }

    [Fact]
    public void AFailedSignInDidNotSucceed()
    {
        Assert.False(AccountEvents.Succeeded(AccountEventKind.FailedSignIn));
        Assert.True(AccountEvents.Succeeded(AccountEventKind.SignIn));
    }

    [Fact]
    public void ASignOutIsASignOut()
    {
        Assert.Equal(
            AccountEventKind.SignOut, AccountEvents.KindOf("Signed out", 200, "Logout"));
    }

    [Fact]
    public void APasswordResetIsItsOwnEvent()
    {
        Assert.Equal(
            AccountEventKind.PasswordChanged,
            AccountEvents.KindOf("Password reset", 200, "Update"));
    }

    [Fact]
    public void AnythingElseOnTheAccountIsJustAnEvent()
    {
        Assert.Equal(
            AccountEventKind.Other, AccountEvents.KindOf("Email confirmed", 200, "Update"));
    }
}
