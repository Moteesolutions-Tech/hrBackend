using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Notifications;
using Motee.Infrastructure.Notifications;

namespace Motee.Tests.Notifications;

public class EmailDispatcherTests
{
    private sealed record Greeting(string Name);

    private sealed record Unregistered(string Anything);

    private sealed class GreetingTemplate : IEmailTemplate<Greeting>
    {
        public EmailContent Render(Greeting model) => new()
        {
            Subject = $"Hello {model.Name}",
            TextBody = $"Hello {model.Name}, plain.",
            HtmlBody = $"<p>Hello {model.Name}, rich.</p>",
        };
    }

    private sealed class RecordingQueue : IEmailQueue
    {
        public List<EmailMessage> Sent { get; } = [];

        public void Enqueue(EmailMessage message) => Sent.Add(message);
    }

    private static (IEmailDispatcher Dispatcher, RecordingQueue Queue) Build()
    {
        RecordingQueue queue = new();

        ServiceCollection services = new();
        services.AddSingleton<IEmailQueue>(queue);
        services.AddScoped<IEmailTemplate<Greeting>, GreetingTemplate>();
        services.AddScoped<IEmailDispatcher, EmailDispatcher>();

        return (services.BuildServiceProvider().GetRequiredService<IEmailDispatcher>(), queue);
    }

    [Fact]
    public void TheRenderedMessageCarriesTheRecipientAndBothBodies()
    {
        (IEmailDispatcher dispatcher, RecordingQueue queue) = Build();

        dispatcher.Send("ada@acme.com", new Greeting("Ada"));

        EmailMessage message = Assert.Single(queue.Sent);

        Assert.Equal("ada@acme.com", message.To);
        Assert.Equal("Hello Ada", message.Subject);
        Assert.Equal("Hello Ada, plain.", message.TextBody);
        Assert.Contains("Hello Ada, rich.", message.HtmlBody!, StringComparison.Ordinal);
    }

    // The template returns a fragment; the chrome is added here. That is what stops a
    // new email shipping without the layout.
    [Fact]
    public void TheLayoutIsAppliedByTheDispatcherNotTheTemplate()
    {
        (IEmailDispatcher dispatcher, RecordingQueue queue) = Build();

        dispatcher.Send("ada@acme.com", new Greeting("Ada"));

        Assert.StartsWith("<!doctype html>", queue.Sent.Single().HtmlBody!, StringComparison.Ordinal);
    }

    // A model with no template is a wiring mistake. Failing loudly here beats an email
    // type that silently never sends - which is indistinguishable from a delivery
    // problem once it is in production.
    [Fact]
    public void AModelWithNoTemplateThrowsAndNamesTheFix()
    {
        (IEmailDispatcher dispatcher, RecordingQueue queue) = Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => dispatcher.Send("ada@acme.com", new Unregistered("x")));

        Assert.Contains("Unregistered", error.Message, StringComparison.Ordinal);
        Assert.Empty(queue.Sent);
    }
}
