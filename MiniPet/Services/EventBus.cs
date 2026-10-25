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
    PetReflectStateChanged,
    // ── V0.1.3 多桌宠命令（payload: Models.PetCommandEvent；由 PetManager 订阅执行）──
    AddPet,
    RemovePet,
    SetPrimaryPet,
    // 设置在设置中心点「应用到桌宠」：payload Models.PetPaperdollEvent，只有目标宠物窗口执行换装
    PaperdollApplied,
    // 设置在设置中心点「确定应用」（Mob/Npc）：payload Models.PetEntityAppliedEvent，只有目标宠物窗口切实体
    EntityApplied,
    // 换装完成回执：payload string petId（发起方设置窗口据此解除「合成中」按钮态）
    PaperdollAppliedDone
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
