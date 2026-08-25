namespace Motee.Application.Notifications;

// The one thing business code calls. It finds the template for the model, renders it,
// applies the shared layout and queues the result.
//
// Rendering happens here rather than in the background job on purpose: the queue
// serialises what it is given, and a rendered record survives that trivially while a
// polymorphic model would need type information baked into the payload - which breaks
// the moment a model is renamed and leaves undeliverable jobs in the queue.
public interface IEmailDispatcher
{
    void Send<TModel>(string to, TModel model)
        where TModel : notnull;
}
