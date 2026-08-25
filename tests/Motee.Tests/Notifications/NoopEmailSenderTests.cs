using Microsoft.Extensions.Logging;
using Motee.Application.Common;
using Motee.Application.Notifications;
using Motee.Infrastructure.Notifications;

namespace Motee.Tests.Notifications;

// The sender that runs whenever no provider is configured. Which of its two behaviours
// you get is the difference between a developer reading an OTP off the console and a
// deployment silently dropping every verification email, so both are pinned here.
public class NoopEmailSenderTests
{
    private sealed class StubDebugMode(bool enabled) : IDebugMode
    {
        public bool Enabled { get; } = enabled;
    }

    private sealed record Entry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<NoopEmailSender>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
    }

    private static EmailMessage Message() => new()
    {
        To = "ada@acme.com",
        Subject = "Your verification code",
        TextBody = "Your Motee verification code is 123456. It expires in 10 minutes.",
    };

    // The point of debug mode for email: the body carries the code and the invite link,
    // so a developer can drive the whole flow without a mailbox.
    [Fact]
    public async Task TheBodyIsLoggedWhenDebugModeIsOn()
    {
        CapturingLogger logger = new();
        NoopEmailSender sender = new(logger, new StubDebugMode(enabled: true));

        await sender.SendAsync(Message());

        Entry entry = Assert.Single(logger.Entries);
        Assert.Contains("123456", entry.Message, StringComparison.Ordinal);
        Assert.Contains("ada@acme.com", entry.Message, StringComparison.Ordinal);
    }

    // Not a warning on a developer's machine: there is nothing wrong, and a warning
    // here trains people to ignore the one below.
    [Fact]
    public async Task ItIsNotAWarningWhenDebugModeIsOn()
    {
        CapturingLogger logger = new();
        NoopEmailSender sender = new(logger, new StubDebugMode(enabled: true));

        await sender.SendAsync(Message());

        Assert.Equal(LogLevel.Information, Assert.Single(logger.Entries).Level);
    }

    // On a server this means every verification email is being dropped, so it has to be
    // loud enough to notice.
    [Fact]
    public async Task ItWarnsWhenDebugModeIsOff()
    {
        CapturingLogger logger = new();
        NoopEmailSender sender = new(logger, new StubDebugMode(enabled: false));

        await sender.SendAsync(Message());

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    // Codes and invite links must not reach a deployed log, which is the whole reason
    // the body is gated rather than always printed.
    [Fact]
    public async Task TheBodyIsNotLoggedWhenDebugModeIsOff()
    {
        CapturingLogger logger = new();
        NoopEmailSender sender = new(logger, new StubDebugMode(enabled: false));

        await sender.SendAsync(Message());

        Assert.DoesNotContain("123456", Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
    }
}
