using System;
using System.Collections.Generic;
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
        private const char KeySep = '|';

        public SpriteService(CacheManager cacheManager)
        {
            _cacheManager = cacheManager;
            _memoryCache = new Dictionary<string, LRUEntry>();
            _accessOrder = new LinkedList<string>();
        }

        /// <summary>
        /// 从缓存加载精灵图条
        /// </summary>
        public SpriteStrip? LoadSpriteStrip(string mobId, string action)
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

                // 校验：退化空动作（1×1 等无效画面）视为损坏，删除磁盘缓存并返回 null，由 AnimService fallback
                if (strip.FrameWidth <= 1 || strip.FrameHeight <= 1)
                {
                    _cacheManager.DeleteSpriteStrip(mobId, action);
                    Console.WriteLine($"[SpriteService] LoadSpriteStrip({mobId}/{action}): 空动作，已清理坏缓存");
                    return null;
                }

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
        public void CacheStrip(string mobId, string action, SpriteStrip strip, byte[]? pngData)
        {
            if (pngData == null || pngData.Length == 0) return;

            // 重活（整条 PNG 解码 + 逐帧像素裁剪 + 逐帧 PNG 重编码）放在锁外执行，
            // 否则会与 UI 帧循环的 GetCachedStrip/GetFramePng 争用 _lock，
            // 后台构建多个动作时 UI 线程长时间等锁 → 掉帧卡顿。
            var frames = BuildFramesCache(action, strip, pngData);

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

                // 锁内只做字典合并（极快），不再做像素解码/裁剪/重编码
                foreach (var kv in frames)
                {
                    _framesCache[kv.Key] = kv.Value;
                }
            }
        }

        /// <summary>
        /// 预裁剪精灵图条的所有帧（锁外执行）：解码整条 PNG → 逐帧 BGRA 裁剪 → 逐帧 PNG 重编码。
        /// 返回 action_frameIndex → PNG 字节的帧表，供 CacheStrip 在锁内快速合并。
        /// </summary>
        private static Dictionary<string, byte[]> BuildFramesCache(string action, SpriteStrip strip, byte[] pngData)
        {
            var result = new Dictionary<string, byte[]>();
            try
            {
                var (bgra, width, height) = PngEncoder.DecodePng(pngData);
                if (bgra.Length == 0 || width <= 0 || height <= 0) return result;

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
                        result[MakeFrameKey(action, i)] = framePng;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SpriteService] BuildFramesCache({action}): {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 获取某 mob 当前可用的动作（内存缓存 ∪ 磁盘缓存）
        /// </summary>
        public List<string> GetAvailableActions(string mobId)
        {
            var actions = new HashSet<string>();
            lock (_lock)
            {
                foreach (var key in _memoryCache.Keys)
                {
                    var parts = key.Split(KeySep);
                    if (parts.Length == 2 && parts[0] == mobId) actions.Add(parts[1]);
                }
            }
            foreach (var a in _cacheManager.GetCachedActions(mobId)) actions.Add(a);
            return actions.ToList();
        }

        /// <summary>
        /// 从内存缓存读取
        /// </summary>
        public (SpriteStrip?, byte[]?) GetCachedStrip(string mobId, string action)
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
        /// 从 WZ 预加载怪物精灵图到缓存（优先读缓存，缓存无数据再走 WZ）
        /// </summary>
        public bool PreloadMob(WzService? wz, string mobId, string type = "Mob")
        {
            try
            {
                // 1. 尝试从磁盘缓存加载常用动作
                var cachedActions = new[] { "stand", "move", "fly", "walk", "jump" };
                bool loadedFromCache = false;
                foreach (var action in cachedActions)
                {
                    if (_cacheManager.IsSpriteCached(mobId, action))
                    {
                        var cached = _cacheManager.LoadSpriteStrip(mobId, action);
                        if (cached != null)
                        {
                            var (pngData, configData) = cached.Value;
                            var config = JsonSerializer.Deserialize<PetConfig>(new ReadOnlySpan<byte>(configData));
                            if (config != null && config.Sprites.TryGetValue(action, out var strip))
                            {
                                CacheStrip(mobId, action, strip, pngData);
                                loadedFromCache = true;
                            }
                        }
                    }
                }
                if (loadedFromCache)
                {
                    Console.WriteLine($"[SpriteService] PreloadMob({mobId}): 从缓存加载完成");
                    return true;
                }

                // 2. 缓存没有，从 WZ 加载（需要 WZ 可用）
                if (wz == null || !wz.IsLoaded) return false;

                var actions = wz.GetActionList(mobId, type);
                if (actions.Count == 0) return false;

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
        private readonly List<(WzService? wz, string mobId, string action, string type)> _bgQueue = new();
        private bool _bgQueueRunning;

        private void ProcessBgQueue()
        {
            (WzService? wz, string mobId, string action, string type)? item;
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
                if (GetCachedStrip(mobId, action).Item1 == null && wz != null && wz.IsLoaded)
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
        public bool PreloadAction(WzService? wz, string mobId, string action, string type = "Mob")
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
                        // 校验退化空动作（1×1 等无效画面）：清理坏缓存，落到下面走 WZ 重建
                        if (strip.FrameWidth <= 1 || strip.FrameHeight <= 1)
                        {
                            _cacheManager.DeleteSpriteStrip(mobId, action);
                            Console.WriteLine($"[SpriteService] PreloadAction({mobId}/{action}): 空动作，清理坏缓存");
                        }
                        else
                        {
                            CacheStrip(mobId, action, strip, pngData);
                            return true;
                        }
                    }
                }

                // 2. 缓存未命中，从 WZ 加载（需要 WZ 可用）
                if (wz == null || !wz.IsLoaded) return false;
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
        public List<string> GetLoadedMobs()
        {
            lock (_lock)
            {
                return _memoryCache.Keys
                    .Select(k => k.Split(KeySep)[0])
                    .Distinct()
                    .ToList();
            }
        }

        private static string MakeKey(string mobId, string action) => $"{mobId}{KeySep}{action}";
        private static string MakeFrameKey(string action, int frameIndex) => $"{action}_{frameIndex}";

        private void EvictIfNeeded(string incomingMobId)
        {
            var mobs = _memoryCache.Keys
                .Select(k => k.Split(KeySep)[0])
                .Distinct()
                .Count();

            if (mobs < MaxMobCache) return;

            var node = _accessOrder.Last;
            while (node != null && mobs >= MaxMobCache)
            {
                var evictKey = node.Value;
                var next = node.Previous;

                var parts = evictKey.Split(KeySep, 2);
                var evictMobId = parts[0];
                var evictAction = parts.Length > 1 ? parts[1] : string.Empty;
                if (evictMobId != incomingMobId)
                {
                    // 同时清理该 action 的帧缓存
                    var keysToRemove = _framesCache.Keys
                        .Where(k => k.StartsWith(evictAction + "_"))
                        .ToList();
                    foreach (var k in keysToRemove) _framesCache.Remove(k);

                    _memoryCache.Remove(evictKey);
                    _accessOrder.Remove(node);
                    mobs = _memoryCache.Keys
                        .Select(k => k.Split(KeySep)[0])
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
