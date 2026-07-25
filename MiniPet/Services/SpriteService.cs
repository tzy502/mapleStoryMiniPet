using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using MiniPet.Models;

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
                // 先查内存缓存
                var (cachedStrip, cachedPng) = GetCachedStrip(mobId, action);
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

                // 缓存到内存
                CacheStrip(mobId, action, strip, pngData);

                return strip;
            }
        }

        /// <summary>
        /// 裁剪第 i 帧
        /// </summary>
        public Bitmap? GetFrame(SpriteStrip strip, byte[]? pngData, int frameIndex)
        {
            if (pngData == null || pngData.Length == 0) return null;
            if (frameIndex < 0 || frameIndex >= strip.FrameCount) return null;

            try
            {
                using var ms = new MemoryStream(pngData);
                using var fullImage = Image.FromStream(ms) as Bitmap;
                if (fullImage == null) return null;

                var x = frameIndex * strip.FrameWidth;
                var rect = new Rectangle(x, 0, strip.FrameWidth, strip.FrameHeight);

                // 限制裁剪区域不超出图片边界
                if (rect.Right > fullImage.Width || rect.Bottom > fullImage.Height)
                    return null;

                return fullImage.Clone(rect, fullImage.PixelFormat);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 预加载指定 mob 的所有动作
        /// </summary>
        public Dictionary<string, SpriteStrip> PreloadSprites(int mobId)
        {
            var result = new Dictionary<string, SpriteStrip>();

            lock (_lock)
            {
                // 尝试加载已知动作列表
                var configResult = _cacheManager.LoadSpriteStrip(mobId, "config");
                if (configResult == null) return result;

                var (_, configData) = configResult.Value;
                var config = JsonSerializer.Deserialize<PetConfig>(new ReadOnlySpan<byte>(configData));
                if (config?.Sprites == null) return result;

                foreach (var (action, strip) in config.Sprites)
                {
                    var loaded = LoadSpriteStrip(mobId, action);
                    if (loaded != null)
                    {
                        result[action] = loaded;
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// 缓存精灵图条到内存（含 PNG 数据）
        /// </summary>
        public void CacheStrip(int mobId, string action, SpriteStrip strip, byte[]? pngData)
        {
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
        public bool PreloadMob(WzService wz, int mobId)
        {
            try
            {
                var actions = wz.GetActionList(mobId);
                if (actions.Count == 0) return false;

                // 优先 stand/move/fly，其次第一个可用动作
                var priorityActions = actions.Where(a =>
                    a is "stand" or "move" or "fly" or "walk" or "jump").ToList();
                if (priorityActions.Count == 0 && actions.Count > 0)
                    priorityActions.Add(actions[0]);

                var loaded = false;
                foreach (var action in priorityActions)
                {
                    try
                    {
                        var pngData = wz.BuildSpriteStripPng(mobId, action);
                        if (pngData == null) continue;

                        var (strip, config) = wz.ExportSpriteStrip(mobId, action);
                        if (strip == null || config == null) continue;

                        var configJson = System.Text.Json.JsonSerializer.Serialize(config);
                        var configBytes = System.Text.Encoding.UTF8.GetBytes(configJson);

                        _cacheManager.SaveSpriteStrip(mobId, action, pngData, configBytes);
                        CacheStrip(mobId, action, strip, pngData);
                        loaded = true;
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[SpriteService] PreloadMob action: {ex.Message}"); }
                }

                return loaded;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SpriteService] PreloadMob({mobId}): {ex.Message}");
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

        private static string MakeKey(int mobId, string action)
        {
            return $"{mobId}_{action}";
        }

        private void EvictIfNeeded(int incomingMobId)
        {
            // 统计当前缓存的实体数
            var mobs = _memoryCache.Keys
                .Select(k => k.Split('_')[0])
                .Distinct()
                .Count();

            if (mobs < MaxMobCache) return;

            // 从 LRU 尾部淘汰，直到有空间
            var node = _accessOrder.Last;
            while (node != null && mobs >= MaxMobCache)
            {
                var evictKey = node.Value;
                var next = node.Previous;

                var evictMobId = evictKey.Split('_')[0];
                // 如果要淘汰的 mob 不是 incoming 的 mob，则淘汰
                if (evictMobId != incomingMobId.ToString())
                {
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