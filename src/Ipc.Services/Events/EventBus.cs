using System.Collections.Concurrent;

namespace Ipc.Services.Events;

public interface IEventBus
{
    void Subscribe<T>(Action<T> handler);

    void Publish<T>(T @event);
}

public sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();

    public void Subscribe<T>(Action<T> handler)
    {
        _handlers.AddOrUpdate(
            typeof(T),
            _ => new List<Delegate> { handler },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(handler);
                }

                return list;
            });
    }

    public void Publish<T>(T @event)
    {
        if (!_handlers.TryGetValue(typeof(T), out var list))
        {
            return;
        }

        List<Delegate> snapshot;
        lock (list)
        {
            snapshot = list.ToList();
        }

        foreach (var handler in snapshot)
        {
            ((Action<T>)handler)(@event);
        }
    }
}

public interface IEvent
{
}

public sealed record SystemStartedEvent(string SystemName) : IEvent;

public sealed record ObjectCreatedEvent(string Library, string Name, string Type) : IEvent;

public sealed record ObjectDeletedEvent(string Library, string Name, string Type) : IEvent;

public sealed record SignOnAttemptEvent(string User, bool Success) : IEvent;