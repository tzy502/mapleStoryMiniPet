using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MiniPet.Tests;

public class SpriteServiceTests
{
    // --- embedded minimal models ---
    public class TestSpriteStrip
    {
        public string Action { get; set; } = "";
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public int FrameCount { get; set; }
        public int TotalWidth => FrameWidth * FrameCount;
        public int OriginX { get; set; }
        public int OriginY { get; set; }
    }

    public class TestFrameData
    {
        public int Index { get; set; }
        public int Delay { get; set; } = 100;
        public int A0 { get; set; } = 255;
        public int A1 { get; set; } = 255;
    }

    // --- minimal LRU cache replica ---
    public class TestSpriteCache
    {
        private readonly Dictionary<string, TestSpriteStrip> _cache = new();
        private readonly LinkedList<string> _order = new();
        private readonly object _lock = new();
        private const int MaxEntries = 3;

        public void CacheStrip(int mobId, string action, TestSpriteStrip strip)
        {
            lock (_lock)
            {
                var key = MakeKey(mobId, action);
                if (_cache.ContainsKey(key))
                    _order.Remove(key);

                _order.AddFirst(key);
                _cache[key] = strip;

                // Evict LRU if over limit
                while (_cache.Count > MaxEntries)
                {
                    var last = _order.Last!;
                    _order.RemoveLast();
                    _cache.Remove(last.Value);
                }
            }
        }

        public TestSpriteStrip? GetCachedStrip(int mobId, string action)
        {
            lock (_lock)
            {
                var key = MakeKey(mobId, action);
                if (_cache.TryGetValue(key, out var strip))
                {
                    _order.Remove(key);
                    _order.AddFirst(key);
                    return strip;
                }
                return null;
            }
        }

        public void ClearCache()
        {
            lock (_lock)
            {
                _cache.Clear();
                _order.Clear();
            }
        }

        public int Count => _cache.Count;

        public static string MakeKey(int mobId, string action) => $"{mobId}_{action}";
    }

    // --- tests: 正常路径 ---

    [Fact]
    public void GetCachedStrip_CacheHit_ReturnsStrip()
    {
        var cache = new TestSpriteCache();
        var strip = new TestSpriteStrip { Action = "stand", FrameWidth = 100, FrameHeight = 120, FrameCount = 8 };
        cache.CacheStrip(8880150, "stand", strip);

        var cached = cache.GetCachedStrip(8880150, "stand");
        Assert.NotNull(cached);
        Assert.Equal("stand", cached!.Action);
        Assert.Equal(8, cached.FrameCount);
    }

    [Fact]
    public void GetCachedStrip_CacheMiss_ReturnsNull()
    {
        var cache = new TestSpriteCache();
        var result = cache.GetCachedStrip(8880150, "nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public void CacheStrip_AlreadyExists_UpdatesOrderAndKeepsSingleEntry()
    {
        var cache = new TestSpriteCache();
        var strip1 = new TestSpriteStrip { Action = "stand", FrameCount = 8 };
        var strip2 = new TestSpriteStrip { Action = "stand", FrameCount = 16 };

        cache.CacheStrip(8880150, "stand", strip1);
        cache.CacheStrip(8880150, "stand", strip2); // same key, overwrite

        var cached = cache.GetCachedStrip(8880150, "stand");
        Assert.Equal(1, cache.Count);
        Assert.Equal(16, cached!.FrameCount);
    }

    [Fact]
    public void FrameIndex_WithinBounds_IsValid()
    {
        var strip = new TestSpriteStrip { FrameWidth = 100, FrameHeight = 100, FrameCount = 8 };
        // frame index 0..7 are valid
        Assert.True(IsFrameInBounds(strip, 0));
        Assert.True(IsFrameInBounds(strip, 7));
    }

    // --- tests: 边界 ---

    [Fact]
    public void TotalWidth_ZeroFrames_ReturnsZero()
    {
        var strip = new TestSpriteStrip { FrameWidth = 100, FrameCount = 0 };
        Assert.Equal(0, strip.TotalWidth);
    }

    [Fact]
    public void FrameIndex_Negative_OutOfBounds()
    {
        var strip = new TestSpriteStrip { FrameWidth = 100, FrameHeight = 100, FrameCount = 8 };
        Assert.False(IsFrameInBounds(strip, -1));
    }

    [Fact]
    public void FrameIndex_EqualFrameCount_OutOfBounds()
    {
        var strip = new TestSpriteStrip { FrameWidth = 100, FrameHeight = 100, FrameCount = 8 };
        Assert.False(IsFrameInBounds(strip, 8));
    }

    [Fact]
    public void LRUCache_EvictsOldest_WhenOverLimit()
    {
        var cache = new TestSpriteCache();

        cache.CacheStrip(1, "stand", new TestSpriteStrip { Action = "stand" });
        cache.CacheStrip(2, "stand", new TestSpriteStrip { Action = "stand" });
        cache.CacheStrip(3, "stand", new TestSpriteStrip { Action = "stand" });

        // Access mob 1 to make it recently used
        var accessed = cache.GetCachedStrip(1, "stand");
        Assert.NotNull(accessed);

        // Now add mob 4, should evict mob 2 (oldest LRU)
        cache.CacheStrip(4, "stand", new TestSpriteStrip { Action = "stand" });

        // mob 1 and 4 should still be cached, mob 2 evicted
        Assert.NotNull(cache.GetCachedStrip(1, "stand"));
        Assert.Null(cache.GetCachedStrip(2, "stand"));
        Assert.NotNull(cache.GetCachedStrip(3, "stand"));
        Assert.NotNull(cache.GetCachedStrip(4, "stand"));
    }

    // --- tests: 异常 ---

    [Fact]
    public void ClearCache_EmptyCache_DoesNotThrow()
    {
        var cache = new TestSpriteCache();
        var ex = Record.Exception(() => cache.ClearCache());
        Assert.Null(ex);
    }

    [Fact]
    public void ClearCache_AfterPopulating_EmptiesCache()
    {
        var cache = new TestSpriteCache();
        cache.CacheStrip(8880150, "stand", new TestSpriteStrip { Action = "stand" });
        cache.CacheStrip(8880150, "move", new TestSpriteStrip { Action = "move" });

        Assert.Equal(2, cache.Count);
        cache.ClearCache();
        Assert.Equal(0, cache.Count);
        Assert.Null(cache.GetCachedStrip(8880150, "stand"));
    }

    [Fact]
    public void MakeKey_ProducesExpectedFormat()
    {
        var key = TestSpriteCache.MakeKey(8880150, "stand");
        Assert.Equal("8880150_stand", key);

        var key2 = TestSpriteCache.MakeKey(0, "");
        Assert.Equal("0_", key2);
    }

    // --- helpers ---

    private static bool IsFrameInBounds(TestSpriteStrip strip, int frameIndex)
        => frameIndex >= 0 && frameIndex < strip.FrameCount;
}
