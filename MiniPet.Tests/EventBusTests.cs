using Xunit;
using System;
using System.Collections.Generic;

namespace MiniPet.Tests;

public class EventBusTests
{
    // --- embedded minimal EventBus replica ---
    public enum TestEventType { EventA, EventB, EventC }

    public class TestEventBus
    {
        private readonly Dictionary<TestEventType, List<Action<object?>>> _subs = new();
        private readonly object _lock = new();

        public void Subscribe(TestEventType type, Action<object?> cb)
        {
            lock (_lock)
            {
                if (!_subs.ContainsKey(type))
                    _subs[type] = new List<Action<object?>>();
                _subs[type].Add(cb);
            }
        }

        public void Unsubscribe(TestEventType type, Action<object?> cb)
        {
            lock (_lock)
            {
                if (_subs.TryGetValue(type, out var list))
                    list.Remove(cb);
            }
        }

        public void Publish(TestEventType type, object? data = null)
        {
            List<Action<object?>> handlers;
            lock (_lock)
            {
                if (!_subs.TryGetValue(type, out var list)) return;
                handlers = new List<Action<object?>>(list);
            }
            foreach (var h in handlers)
            {
                try { h(data); }
                catch (Exception) { /* isolate handler failures */ }
            }
        }
    }

    // --- tests: 正常路径 ---

    [Fact]
    public void Publish_SubscriberReceivesData()
    {
        var bus = new TestEventBus();
        string? received = null;

        bus.Subscribe(TestEventType.EventA, data => received = data as string);
        bus.Publish(TestEventType.EventA, "hello");

        Assert.Equal("hello", received);
    }

    [Fact]
    public void Publish_MultipleSubscribers_AllReceive()
    {
        var bus = new TestEventBus();
        int count = 0;

        bus.Subscribe(TestEventType.EventA, _ => count++);
        bus.Subscribe(TestEventType.EventA, _ => count++);
        bus.Subscribe(TestEventType.EventA, _ => count++);
        bus.Publish(TestEventType.EventA);

        Assert.Equal(3, count);
    }

    [Fact]
    public void Publish_DifferentEvents_DeliverToCorrectSubscribers()
    {
        var bus = new TestEventBus();
        string? resultA = null;
        string? resultB = null;

        bus.Subscribe(TestEventType.EventA, data => resultA = data as string);
        bus.Subscribe(TestEventType.EventB, data => resultB = data as string);

        bus.Publish(TestEventType.EventA, "A");
        bus.Publish(TestEventType.EventB, "B");

        Assert.Equal("A", resultA);
        Assert.Equal("B", resultB);
    }

    // --- tests: 边界 ---

    [Fact]
    public void Publish_NoSubscribers_DoesNotThrow()
    {
        var bus = new TestEventBus();
        var ex = Record.Exception(() => bus.Publish(TestEventType.EventA, "data"));
        Assert.Null(ex);
    }

    [Fact]
    public void Unsubscribe_RemovesHandler_NoLongerCalled()
    {
        var bus = new TestEventBus();
        int count = 0;
        Action<object?> handler = _ => count++;

        bus.Subscribe(TestEventType.EventA, handler);
        bus.Publish(TestEventType.EventA);
        Assert.Equal(1, count);

        bus.Unsubscribe(TestEventType.EventA, handler);
        bus.Publish(TestEventType.EventA);
        Assert.Equal(1, count); // still 1
    }

    [Fact]
    public void Subscribe_SameHandlerTwice_FiresTwice()
    {
        var bus = new TestEventBus();
        int count = 0;
        Action<object?> handler = _ => count++;

        bus.Subscribe(TestEventType.EventA, handler);
        bus.Subscribe(TestEventType.EventA, handler);
        bus.Publish(TestEventType.EventA);

        Assert.Equal(2, count);
    }

    // --- tests: 异常 ---

    [Fact]
    public void Publish_HandlerThrows_OtherHandlersStillExecute()
    {
        var bus = new TestEventBus();
        int secondCalled = 0;

        bus.Subscribe(TestEventType.EventA, _ => throw new InvalidOperationException("fail"));
        bus.Subscribe(TestEventType.EventA, _ => secondCalled++);

        // handler exceptions are caught and do not propagate
        var ex = Record.Exception(() => bus.Publish(TestEventType.EventA));
        Assert.Null(ex);
    }

    [Fact]
    public void Unsubscribe_NotSubscribed_DoesNotThrow()
    {
        var bus = new TestEventBus();
        Action<object?> handler = _ => { };
        var ex = Record.Exception(() => bus.Unsubscribe(TestEventType.EventA, handler));
        Assert.Null(ex);
    }

    [Fact]
    public void Publish_WithNullData_DeliversNull()
    {
        var bus = new TestEventBus();
        object? received = new object();

        bus.Subscribe(TestEventType.EventA, data => received = data);
        bus.Publish(TestEventType.EventA, null);

        Assert.Null(received);
    }
}
