using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MiniPet.Models;
using MiniPet.Utils;

namespace MiniPet.Services
{
    /// <summary>
    /// 精灵图服务 - 精灵图加载、帧缓存、内存管理
    /// 与 CacheManager 分工：CacheManager 管磁盘缓存，SpriteService 管内存缓存
    /// </summary>
    public class SpriteService
    {
        private readonly CacheManager _cacheManager;
        private readonly Dictionary<string, LRUEntry> _memoryCache;
        private readonly LinkedList<string> _accessOrder;
        private readonly object _lock = new();
        private const int MaxMobCache = 3;

        public SpriteService(CacheManager cacheManager)
        {
            _cacheManager = cacheManager;
            _memoryCache = new Dictionary<string, LRUEntry>();
            _accessOrder = new LinkedList<string>();
        }

        /// <summary>
        /// 从缓存加载精灵图条
        /// </summary>
        public SpriteStrip? LoadSpriteStrip(int mobId, string action)
        {
            lock (_lock)
            {
                // 先查内存缓存（缓存的帧数据中有 strip 即可）
                var (cachedStrip, _) = GetCachedStrip(mobId, action);
                if (cachedStrip != null) return cachedStrip;

                // 从磁盘缓存加载
                var result = _cacheManager.LoadSpriteStrip(mobId, action);
                if (result == null) return null;

                var (pngData, configData) = result.Value;

                // 解析 config
                var config = JsonSerializer.Deserialize<PetConfig>(new ReadOnlySpan<byte>(configData));
                if (config == null) return null;

                if (!config.Sprites.TryGetValue(action, out var strip))
                    return null;

                // 缓存到内存（含预裁剪帧）
                CacheStrip(mobId, action, strip, pngData);

                return strip;
            }
        }

        /// <summary>
        /// 获取第 frameIndex 帧的 PNG 字节（直接从预裁剪帧缓存返回，不解码不裁剪）
        /// </summary>
        public byte[]? GetFramePng(SpriteStrip strip, int frameIndex, byte[]? pngData)
        {
            if (strip == null) return null;
            if (frameIndex < 0 || frameIndex >= strip.FrameCount) return null;

            // 从预裁剪帧缓存中直接返回
            lock (_lock)
            {
                var key = MakeFrameKey(strip.Action, frameIndex);
                if (_framesCache.TryGetValue(key, out var cached))
                    return cached;
            }
            return null;
        }

        /// <summary>
        /// 预裁剪帧缓存：key = "action_frameIndex" → byte[]
        /// </summary>
        private readonly Dictionary<string, byte[]> _framesCache = new();

        /// <summary>
        /// 缓存精灵图条到内存（含 PNG 数据和预裁剪帧）
        /// </summary>
        public void CacheStrip(int mobId, string action, SpriteStrip strip, byte[]? pngData)
        {
            if (pngData == null || pngData.Length == 0) return;

            lock (_lock)
            {
                var key = MakeKey(mobId, action);
                var entry = new LRUEntry
                {
                    Strip = strip,
                    PngData = pngData
                };

                // 已存在则更新访问顺序
                if (_memoryCache.ContainsKey(key))
                {
                    _accessOrder.Remove(key);
                }
                else
                {
                    // LRU 淘汰：超过最大实体数时移除最久未访问的
                    EvictIfNeeded(mobId);
                }

                _accessOrder.AddFirst(key);
                _memoryCache[key] = entry;

                // 预裁剪所有帧并缓存
                CacheFrames(mobId, action, strip, pngData);
            }
        }

        /// <summary>
        /// 预裁剪精灵图条的所有帧，存入帧缓存
        /// </summary>
        private void CacheFrames(int mobId, string action, SpriteStrip strip, byte[] pngData)
        {
            try
            {
                var (bgra, width, height) = PngEncoder.DecodePng(pngData);
                if (bgra.Length == 0 || width <= 0 || height <= 0) return;

                int fw = strip.FrameWidth;
                int fh = strip.FrameHeight;

                for (int i = 0; i < strip.FrameCount; i++)
                {
                    int srcX = i * fw;
                    if (srcX + fw > width || fh > height) continue;

                    var frameBgra = new byte[fw * fh * 4];
                    for (int y = 0; y < fh; y++)
                    {
                        int srcOffset = (y * width + srcX) * 4;
                        int dstOffset = y * fw * 4;
                        Buffer.BlockCopy(bgra, srcOffset, frameBgra, dstOffset, fw * 4);
                    }

                    var framePng = PngEncoder.Encode(frameBgra, fw, fh);
                    if (framePng != null)
                    {
                        _framesCache[MakeFrameKey(action, i)] = framePng;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SpriteService] CacheFrames({mobId}/{action}): {ex.Message}");
            }
        }

        /// <summary>
        /// 从内存缓存读取
        /// </summary>
        public (SpriteStrip?, byte[]?) GetCachedStrip(int mobId, string action)
        {
            lock (_lock)
            {
                var key = MakeKey(mobId, action);
                if (_memoryCache.TryGetValue(key, out var entry))
                {
                    // 更新访问顺序
                    _accessOrder.Remove(key);
                    _accessOrder.AddFirst(key);
                    return (entry.Strip, entry.PngData);
                }
                return (null, null);
            }
        }

        /// <summary>
        /// 从 WZ 预加载怪物精灵图到缓存（提取实际 PNG 帧并拼接精灵图条）
        /// </summary>
        public bool PreloadMob(WzService wz, int mobId, string type = "Mob")
        {
            try
            {
                var actions = wz.GetActionList(mobId, type);
                if (actions.Count == 0) return false;

                // 预加载优先级动作（基础动作），确保快速显示
                var preloadOrder = new[] { "stand", "move", "fly", "walk", "jump" };
                var priorityActions = preloadOrder.Where(actions.Contains).ToList();
                if (priorityActions.Count == 0 && actions.Count > 0)
                    priorityActions.Add(actions[0]);

                var loaded = false;
                foreach (var action in priorityActions)
                {
                    loaded |= PreloadAction(wz, mobId, action, type);
                }

                // 其余动作放入后台队列
                var otherActions = actions.Except(priorityActions).ToList();
                if (otherActions.Count > 0)
                {
                    lock (_bgQueueLock)
                    {
                        _bgQueue.AddRange(otherActions.Select(a => (wz, mobId, a, type)));
                        if (_bgQueueRunning) return loaded;
                        _bgQueueRunning = true;
                    }
                    ProcessBgQueue();
                }

                return loaded;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SpriteService] PreloadMob({mobId}): {ex.Message}");
                return false;
            }
        }

        private readonly object _bgQueueLock = new();
        private readonly List<(WzService wz, int mobId, string action, string type)> _bgQueue = new();
        private bool _bgQueueRunning;

        private void ProcessBgQueue()
        {
            (WzService wz, int mobId, string action, string type)? item;
            lock (_bgQueueLock)
            {
                if (_bgQueue.Count == 0)
                {
                    _bgQueueRunning = false;
                    return;
                }
                item = _bgQueue[0];
                _bgQueue.RemoveAt(0);
            }

            if (item.HasValue)
            {
                var (wz, mobId, action, type) = item.Value;
                if (GetCachedStrip(mobId, action).Item1 == null)
                {
                    PreloadAction(wz, mobId, action, type);
                }
                System.Threading.Timer? timer = null;
                timer = new System.Threading.Timer(_ =>
                {
                    timer?.Dispose();
                    ProcessBgQueue();
                }, null, 50, System.Threading.Timeout.Infinite);
            }
        }

        /// <summary>
        /// 从 WZ 预加载怪物的指定动作，支持 Mob/NPC 类型。
        /// 优先从磁盘缓存读取，缓存未命中再走 WZ。
        /// </summary>
        public bool PreloadAction(WzService wz, int mobId, string action, string type = "Mob")
        {
            try
            {
                // 1. 先看磁盘缓存
                var cached = _cacheManager.LoadSpriteStrip(mobId, action);
                if (cached != null)
                {
                    var (pngData, configData) = cached.Value;
                    var config = JsonSerializer.Deserialize<PetConfig>(new ReadOnlySpan<byte>(configData));
                    if (config != null && config.Sprites.TryGetValue(action, out var strip))
                    {
                        CacheStrip(mobId, action, strip, pngData);
                        return true;
                    }
                }

                // 2. 缓存未命中，从 WZ 加载
                var pngDataWz = wz.BuildSpriteStripPng(mobId, action, type);
                if (pngDataWz == null) return false;

                var (stripWz, configWz) = wz.ExportSpriteStrip(mobId, action, type);
                if (stripWz == null || configWz == null) return false;

                var configJson = JsonSerializer.Serialize(configWz);
                var configBytes = System.Text.Encoding.UTF8.GetBytes(configJson);

                _cacheManager.SaveSpriteStrip(mobId, action, pngDataWz, configBytes);
                CacheStrip(mobId, action, stripWz, pngDataWz);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SpriteService] PreloadAction({mobId}/{action}/{type}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清理内存缓存
        /// </summary>
        public void ClearCache()
        {
            lock (_lock)
            {
                _memoryCache.Clear();
                _accessOrder.Clear();
                _framesCache.Clear();
            }
        }

        /// <summary>
        /// 获取已加载的 mob ID 列表
        /// </summary>
        public List<int> GetLoadedMobs()
        {
            lock (_lock)
            {
                return _memoryCache.Keys
                    .Select(k => k.Split('_'))
                    .Where(parts => parts.Length == 2 && int.TryParse(parts[0], out _))
                    .Select(parts => int.Parse(parts[0]))
                    .Distinct()
                    .ToList();
            }
        }

        private static string MakeKey(int mobId, string action) => $"{mobId}_{action}";
        private static string MakeFrameKey(string action, int frameIndex) => $"{action}_{frameIndex}";

        private void EvictIfNeeded(int incomingMobId)
        {
            var mobs = _memoryCache.Keys
                .Select(k => k.Split('_')[0])
                .Distinct()
                .Count();

            if (mobs < MaxMobCache) return;

            var node = _accessOrder.Last;
            while (node != null && mobs >= MaxMobCache)
            {
                var evictKey = node.Value;
                var next = node.Previous;

                var evictMobId = evictKey.Split('_')[0];
                if (evictMobId != incomingMobId.ToString())
                {
                    // 同时清理该 mob 的帧缓存
                    var evictAction = evictKey.Split('_', 2)[1];
                    var keysToRemove = _framesCache.Keys
                        .Where(k => k.StartsWith(evictAction + "_"))
                        .ToList();
                    foreach (var k in keysToRemove) _framesCache.Remove(k);

                    _memoryCache.Remove(evictKey);
                    _accessOrder.Remove(node);
                    mobs = _memoryCache.Keys
                        .Select(k => k.Split('_')[0])
                        .Distinct()
                        .Count();
                }

                node = next;
            }
        }

        private class LRUEntry
        {
            public SpriteStrip? Strip { get; set; }
            public byte[]? PngData { get; set; }
        }
    }
}