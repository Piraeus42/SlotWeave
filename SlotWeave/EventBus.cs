using System.Collections.Concurrent;
using Serilog;

namespace SlotWeave;

/// <summary>
/// Loader-internal publish/subscribe event bus.
/// Decouples core modules without crossing the FFI boundary.
/// </summary>
public static class EventBus
{
    private static readonly ConcurrentDictionary<Type, List<Delegate>> Subscribers = new();
    private static readonly ILogger Logger = SlotWeave.Logger.ForContext("SourceContext", "EventBus");

    /// <summary>Subscribe to events of type T.</summary>
    public static void Subscribe<T>(Action<T> handler) where T : notnull
    {
        Subscribers.AddOrUpdate(
            typeof(T),
            _ => [handler],
            (_, list) =>
            {
                lock (list) { list.Add(handler); }
                return list;
            });
    }

    /// <summary>Publish an event to all subscribers of type T.</summary>
    public static void Publish<T>(T evt) where T : notnull
    {
        if (!Subscribers.TryGetValue(typeof(T), out var list)) return;

        Delegate[] handlers;
        lock (list) { handlers = list.ToArray(); }

        foreach (var handler in handlers)
        {
            try
            {
                ((Action<T>)handler)(evt);
            }
            catch (Exception e)
            {
                Logger.Error(e, "EventBus handler for {Type} threw", typeof(T).Name);
            }
        }
    }
}
