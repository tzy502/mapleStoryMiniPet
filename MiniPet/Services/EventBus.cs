using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace MiniPet.Services;

public enum EventType
{
    UserDragStart,
    UserDragEnd,
    ResourceLoaded,
    SettingsChanged,
    ConfigChanged,
    PetChanged,
    OpenSettings,
    AppExit,
    // adapter 发布的 Agent 状态变化（经 PublishOnUIThread marshal 到 UI 线程）
    AgentStateChanged,
    // AgentStateAggregator 计算后的桌宠反映态（已在 UI 线程，直接 Publish）
    PetReflectStateChanged
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

    /// <summary>
    /// 在 UI 线程发布事件：已在 UI 线程则同步调 Publish（保持现有同步语义，无重排/double-queue）；
    /// 否则 Post 到 UI 线程后再 Publish。仅 adapter（AgentStateChanged）使用——不破坏现有 8 个 EventType 的同步 Publish。
    /// </summary>
    public void PublishOnUIThread(EventType eventType, object? data = null)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Publish(eventType, data);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Publish(eventType, data));
        }
    }
}
