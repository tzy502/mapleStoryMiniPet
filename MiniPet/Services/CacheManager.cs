using System;
using System.Collections.Generic;
using System.IO;
using MiniPet.Models;

namespace MiniPet.Services
{
    /// <summary>
    /// 缓存管理器 - 磁盘缓存读写、缓存目录管理、过期清理
    /// </summary>
    public class CacheManager
    {
        private readonly string _cacheDir;
        private readonly Dictionary<string, byte[]> _memoryCache;
        private readonly object _lock = new();
        private long _memoryCacheSize;
        private const long MaxMemoryCacheSize = 128 * 1024 * 1024; // 128MB

        public CacheManager()
        {
            _cacheDir = GetCacheDir();
            _memoryCache = new Dictionary<string, byte[]>();
            _memoryCacheSize = 0;

            // 确保缓存目录存在
            if (!Directory.Exists(_cacheDir))
            {
                Directory.CreateDirectory(_cacheDir);
            }
        }

        /// <summary>
        /// 检查精灵图是否已缓存
        /// </summary>
        public bool IsSpriteCached(int mobId, string action)
        {
            var pngPath = GetSpritePath(mobId, action, "png");
            var configPath = GetSpritePath(mobId, action, "json");
            return File.Exists(pngPath) && File.Exists(configPath);
        }

        /// <summary>
        /// 保存精灵图条
        /// </summary>
        public void SaveSpriteStrip(int mobId, string action, byte[] pngData, byte[] configData)
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(GetSpritePath(mobId, action, "png"));
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var pngPath = GetSpritePath(mobId, action, "png");
                    var configPath = GetSpritePath(mobId, action, "json");

                    File.WriteAllBytes(pngPath, pngData);
                    File.WriteAllBytes(configPath, configData);

                    // 更新内存缓存
                    var key = $"sprite_{mobId}_{action}";
                    AddToMemoryCache(key, pngData);
                }
                catch (IOException)
                {
                    // 忽略写入错误
                }
            }
        }

        /// <summary>
        /// 加载精灵图条
        /// </summary>
        public (byte[] pngData, byte[] configData)? LoadSpriteStrip(int mobId, string action)
        {
            lock (_lock)
            {
                var key = $"sprite_{mobId}_{action}";

                // 先检查内存缓存
                if (_memoryCache.TryGetValue(key, out var cachedPng))
                {
                    var configPath = GetSpritePath(mobId, action, "json");
                    if (File.Exists(configPath))
                    {
                        try
                        {
                            var configData = File.ReadAllBytes(configPath);
                            return (cachedPng, configData);
                        }
                        catch (IOException)
                        {
                            // 文件读取失败
                        }
                    }
                }

                // 从磁盘加载
                try
                {
                    var pngPath = GetSpritePath(mobId, action, "png");
                    var configPath = GetSpritePath(mobId, action, "json");

                    if (File.Exists(pngPath) && File.Exists(configPath))
                    {
                        var pngData = File.ReadAllBytes(pngPath);
                        var configData = File.ReadAllBytes(configPath);

                        // 添加到内存缓存
                        AddToMemoryCache(key, pngData);

                        return (pngData, configData);
                    }
                }
                catch (IOException)
                {
                    // 文件读取失败
                }

                return null;
            }
        }

        /// <summary>
        /// 检查气泡是否已缓存
        /// </summary>
        public bool IsBalloonCached(int balloonId)
        {
            var dir = GetBalloonPath(balloonId);
            if (!Directory.Exists(dir)) return false;

            // 检查必要的瓦片是否存在
            var requiredTiles = new[] { "nw.png", "n.png", "ne.png", "w.png", "c.png", "e.png", "sw.png", "s.png", "se.png" };
            foreach (var tile in requiredTiles)
            {
                if (!File.Exists(Path.Combine(dir, tile)))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 保存气泡瓦片
        /// </summary>
        public void SaveBalloonTiles(int balloonId, BalloonTiles tiles)
        {
            lock (_lock)
            {
                try
                {
                    var dir = GetBalloonPath(balloonId);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var tileMap = new Dictionary<string, byte[]>
                    {
                        { "nw.png", tiles.NW },
                        { "n.png", tiles.N },
                        { "ne.png", tiles.NE },
                        { "w.png", tiles.W },
                        { "c.png", tiles.C },
                        { "e.png", tiles.E },
                        { "sw.png", tiles.SW },
                        { "s.png", tiles.S },
                        { "se.png", tiles.SE },
                        { "arrow.png", tiles.Arrow },
                        { "head.png", tiles.Head }
                    };

                    foreach (var (fileName, data) in tileMap)
                    {
                        if (data.Length > 0)
                        {
                            File.WriteAllBytes(Path.Combine(dir, fileName), data);
                        }
                    }
                }
                catch (IOException)
                {
                    // 忽略写入错误
                }
            }
        }

        /// <summary>
        /// 加载气泡瓦片
        /// </summary>
        public BalloonTiles? LoadBalloonTiles(int balloonId)
        {
            lock (_lock)
            {
                try
                {
                    var dir = GetBalloonPath(balloonId);
                    if (!Directory.Exists(dir)) return null;

                    return new BalloonTiles
                    {
                        NW = ReadTileFile(dir, "nw.png"),
                        N = ReadTileFile(dir, "n.png"),
                        NE = ReadTileFile(dir, "ne.png"),
                        W = ReadTileFile(dir, "w.png"),
                        C = ReadTileFile(dir, "c.png"),
                        E = ReadTileFile(dir, "e.png"),
                        SW = ReadTileFile(dir, "sw.png"),
                        S = ReadTileFile(dir, "s.png"),
                        SE = ReadTileFile(dir, "se.png"),
                        Arrow = ReadTileFile(dir, "arrow.png"),
                        Head = ReadTileFile(dir, "head.png")
                    };
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 清理过期缓存
        /// </summary>
        public void ClearOldCache(int daysRetain)
        {
            lock (_lock)
            {
                try
                {
                    var cutoffDate = DateTime.Now.AddDays(-daysRetain);

                    // 清理精灵图缓存
                    var spritesDir = Path.Combine(_cacheDir, "sprites");
                    if (Directory.Exists(spritesDir))
                    {
                        ClearOldFilesInDir(spritesDir, cutoffDate);
                    }

                    // 清理气泡缓存
                    var balloonsDir = Path.Combine(_cacheDir, "balloons");
                    if (Directory.Exists(balloonsDir))
                    {
                        ClearOldFilesInDir(balloonsDir, cutoffDate);
                    }

                    // 清理地图缓存
                    var mapsDir = Path.Combine(_cacheDir, "maps");
                    if (Directory.Exists(mapsDir))
                    {
                        ClearOldFilesInDir(mapsDir, cutoffDate);
                    }
                }
                catch (IOException)
                {
                    // 忽略清理错误
                }
            }
        }

        /// <summary>
        /// 获取缓存目录
        /// </summary>
        public string GetCacheDir()
        {
            if (OperatingSystem.IsMacOS())
            {
                var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, "Library", "Caches", "MiniPet", "v1");
            }
            else if (OperatingSystem.IsWindows())
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "MiniPet", "cache", "v1");
            }
            else
            {
                // Linux/其他平台
                var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                if (!string.IsNullOrEmpty(xdgCache))
                {
                    return Path.Combine(xdgCache, "MiniPet", "v1");
                }
                return Path.Combine(home, ".cache", "MiniPet", "v1");
            }
        }

        private string GetSpritePath(int mobId, string action, string extension)
        {
            return Path.Combine(_cacheDir, "sprites", mobId.ToString(), $"{action}.{extension}");
        }

        private string GetBalloonPath(int balloonId)
        {
            return Path.Combine(_cacheDir, "balloons", balloonId.ToString());
        }

        private void AddToMemoryCache(string key, byte[] data)
        {
            // 检查是否需要清理内存缓存
            while (_memoryCacheSize + data.Length > MaxMemoryCacheSize && _memoryCache.Count > 0)
            {
                // 简单的LRU：移除第一个元素
                var enumerator = _memoryCache.GetEnumerator();
                if (enumerator.MoveNext())
                {
                    var oldKey = enumerator.Current.Key;
                    var evicted = enumerator.Current.Value;
                    _memoryCache.Remove(oldKey);
                    _memoryCacheSize -= evicted.Length;
                }
            }

            // 移除旧值（如果存在）
            if (_memoryCache.TryGetValue(key, out var oldValue))
            {
                _memoryCacheSize -= oldValue.Length;
            }

            _memoryCache[key] = data;
            _memoryCacheSize += data.Length;
        }

        private static byte[] ReadTileFile(string dir, string fileName)
        {
            var path = Path.Combine(dir, fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        }

        private static void ClearOldFilesInDir(string directory, DateTime cutoffDate)
        {
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var lastWrite = File.GetLastWriteTime(file);
                    if (lastWrite < cutoffDate)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // 忽略单个文件删除错误
                }
            }

            // 清理空目录
            foreach (var dir in Directory.GetDirectories(directory))
            {
                if (Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == 0)
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch (IOException)
                    {
                        // 忽略删除错误
                    }
                }
            }
        }
    }
}
