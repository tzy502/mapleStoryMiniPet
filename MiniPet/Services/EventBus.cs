using System;
using System.Collections.Generic;

namespace MiniPet.Services;

public enum EventType
{
    UserDragStart,
    UserDragEnd,
    ResourceLoaded,
    SettingsChanged,
    ConfigChanged,
    PetChanged,
    AppExit
}

public class EventBus
{
    private readonly Dictionary<EventType, List<Action<object?>>> _subscribers = new();
    private readonly object _lock = new();

    public void Subscribe(EventType eventType, Action<object?> callback)
    {
        try
        {
            lock (_lock)
            {
                if (!_subscribers.ContainsKey(eventType))
                    _subscribers[eventType] = new List<Action<object?>>();
                _subscribers[eventType].Add(callback);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventBus] Subscribe: {ex.Message}");
        }
    }

    public void Unsubscribe(EventType eventType, Action<object?> callback)
    {
        try
        {
            lock (_lock)
            {
                if (_subscribers.TryGetValue(eventType, out var list))
                    list.Remove(callback);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventBus] Unsubscribe: {ex.Message}");
        }
    }

    public void Publish(EventType eventType, object? data = null)
    {
        List<Action<object?>> handlers;
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(eventType, out var list)) return;
            handlers = new List<Action<object?>>(list);
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(data);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EventBus] Publish {eventType}: {ex.Message}");
            }
        }
    }
}
