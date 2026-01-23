using NArk.Core.Events;

namespace NArk.Core.Extensions;

public static class EventHandlingExtensions
{
    public static async Task SafeHandleEventAsync<T>(this IEnumerable<IEventHandler<T>> handlers, T @event, CancellationToken cancellationToken = default) where T : class
    {
        var handlerTasks =
            handlers.Select(handler => handler.HandleAsync(@event, cancellationToken));

        try
        {
            await Task.WhenAll(handlerTasks);
        }
        catch
        {
            // ignore exceptions from event handlers
        }
    }
}