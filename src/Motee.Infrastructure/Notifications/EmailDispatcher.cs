using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Notifications;

namespace Motee.Infrastructure.Notifications;

internal sealed class EmailDispatcher(
    IServiceProvider services,
    IEmailQueue queue) : IEmailDispatcher
{
    public void Send<TModel>(string to, TModel model)
        where TModel : notnull
    {
        // Required, not optional: a model with no template is a programming error, and
        // returning quietly would mean an email type that silently never sends. The
        // message names the missing registration rather than the interface, because the
        // fix is always a line in DependencyInjection.
        IEmailTemplate<TModel> template = services.GetService<IEmailTemplate<TModel>>()
            ?? throw new InvalidOperationException(
                $"No email template is registered for {typeof(TModel).Name}. "
                + $"Add services.AddScoped<IEmailTemplate<{typeof(TModel).Name}>, "
                + $"{typeof(TModel).Name}Template>() in DependencyInjection.");

        EmailContent content = template.Render(model);

        queue.Enqueue(new EmailMessage
        {
            To = to,
            Subject = content.Subject,
            TextBody = content.TextBody,
            HtmlBody = EmailLayout.Wrap(content.Subject, content.HtmlBody),
        });
    }
}
