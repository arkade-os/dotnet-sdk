namespace NArk.Core.Events;

/// <summary>
/// Handles domain events raised by SDK services.
/// Register implementations via DI to react to events such as
/// <c>PostBatchSessionEvent</c>, <c>PostSweepActionEvent</c>, or <c>PostCoinsSpendActionEvent</c>.
/// </summary>
/// <typeparam name="TEvent">The event type to handle.</typeparam>
public interface IEventHandler<in TEvent> where TEvent : class
{
    /// <summary>
    /// Handles the event asynchronously.
    /// </summary>
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}