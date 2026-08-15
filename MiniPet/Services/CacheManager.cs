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
        private const long MaxMemoryCacheSize = 256 * 1024 * 1024; // 256MB（大素材下 128MB 过小，1~2 个 mob 即超限频繁淘汰）

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
        public bool IsSpriteCached(string mobId, string action)
        {
            var pngPath = GetSpritePath(mobId, action, "png");
            var configPath = GetSpritePath(mobId, action, "json");
            return File.Exists(pngPath) && File.Exists(configPath);
        }

        /// <summary>
        /// 枚举某 mob 在磁盘缓存中的动作列表（*.json 文件名）
        /// </summary>
        public List<string> GetCachedActions(string mobId)
        {
            var result = new List<string>();
            try
            {
                var dir = Path.Combine(_cacheDir, "sprites", mobId);
                if (!Directory.Exists(dir)) return result;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                    result.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (IOException) { }
            return result;
        }

        /// <summary>
        /// 保存精灵图条
        /// </summary>
        public void SaveSpriteStrip(string mobId, string action, byte[] pngData, byte[] configData)
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
        /// 删除精灵图条缓存（png + json + 内存）
        /// </summary>
        public void DeleteSpriteStrip(string mobId, string action)
        {
            lock (_lock)
            {
                try
                {
                    var pngPath = GetSpritePath(mobId, action, "png");
                    var configPath = GetSpritePath(mobId, action, "json");
                    if (File.Exists(pngPath)) File.Delete(pngPath);
                    if (File.Exists(configPath)) File.Delete(configPath);
                    var key = $"sprite_{mobId}_{action}";
                    if (_memoryCache.TryGetValue(key, out var oldPng))
                    {
                        _memoryCache.Remove(key);
                        _memoryCacheSize -= oldPng.Length;
                    }
                }
                catch (IOException) { }
            }
        }

        /// <summary>
        /// 加载精灵图条
        /// </summary>
        public (byte[] pngData, byte[] configData)? LoadSpriteStrip(string mobId, string action)
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
        public bool IsBalloonCached(string balloonId)
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
        public void SaveBalloonTiles(string balloonId, BalloonTiles tiles)
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

                    // 持久化切片 origin（MapleSalon2 拼装依赖；无此文件 → 读缓存时 origin 全 0 → 拼装错位）
                    SaveTileOrigins(dir, tiles);
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
        public BalloonTiles? LoadBalloonTiles(string balloonId)
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
                    }.WithOrigins(LoadTileOrigins(dir));
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 清理过期缓存（D10：精灵 png/json 按条带成对删除——任一侧较新则整对保留，防只删一半悬挂；
        /// 气泡按整目录同生共死；地图为单文件无配对，按时间删）。
        /// </summary>
        public void ClearOldCache(int daysRetain)
        {
            lock (_lock)
            {
                try
                {
                    var cutoffDate = DateTime.Now.AddDays(-daysRetain);

                    // 精灵图缓存：{mobId}/{action}.png + {action}.json 成对判定
                    // （SaveSpriteStrip 同时写两侧，两侧 mtime 应一致；任一侧未过期 → 整对保留）
                    var spritesDir = Path.Combine(_cacheDir, "sprites");
                    if (Directory.Exists(spritesDir))
                    {
                        ClearOldSpritePairs(spritesDir, cutoffDate);
                    }

                    // 气泡缓存：{balloonId}/ 整目录（瓦片 + origin.json）整体过期才删，防半删悬挂瓦片
                    var balloonsDir = Path.Combine(_cacheDir, "balloons");
                    if (Directory.Exists(balloonsDir))
                    {
                        ClearOldBalloonDirs(balloonsDir, cutoffDate);
                    }

                    // 地图缓存：单文件无配对，按时间删
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

        /// <summary>精灵条带成对清理：按文件名主干分组（{stem}.png + {stem}.json），组内任一侧未过期 → 整组保留；全过期 → 成对删除。</summary>
        private static void ClearOldSpritePairs(string spritesDir, DateTime cutoffDate)
        {
            foreach (var mobDir in Directory.GetDirectories(spritesDir))
            {
                try
                {
                    var byStem = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (var f in Directory.GetFiles(mobDir))
                    {
                        var stem = Path.GetFileNameWithoutExtension(f);
                        if (!byStem.TryGetValue(stem, out var list))
                        {
                            list = new List<string>();
                            byStem[stem] = list;
                        }
                        list.Add(f);
                    }
                    foreach (var files in byStem.Values)
                    {
                        try
                        {
                            var anyFresh = false;
                            foreach (var f in files)
                            {
                                if (File.GetLastWriteTime(f) >= cutoffDate)
                                {
                                    anyFresh = true;
                                    break;
                                }
                            }
                            if (anyFresh) continue;
                            foreach (var f in files)
                            {
                                File.Delete(f);
                            }
                        }
                        catch (IOException)
                        {
                            // 忽略单个文件删除错误
                        }
                    }
                    // 清空子目录
                    if (Directory.GetFiles(mobDir).Length == 0)
                    {
                        Directory.Delete(mobDir);
                    }
                }
                catch (IOException)
                {
                    // 忽略目录级错误
                }
            }
        }

        /// <summary>气泡整目录清理：目录内任一文件未过期 → 保留；全过期 → 整目录删除（瓦片 + origin.json 同生共死）。</summary>
        private static void ClearOldBalloonDirs(string balloonsDir, DateTime cutoffDate)
        {
            foreach (var dir in Directory.GetDirectories(balloonsDir))
            {
                try
                {
                    var files = Directory.GetFiles(dir);
                    if (files.Length == 0)
                    {
                        Directory.Delete(dir);
                        continue;
                    }
                    var anyFresh = false;
                    foreach (var f in files)
                    {
                        if (File.GetLastWriteTime(f) >= cutoffDate)
                        {
                            anyFresh = true;
                            break;
                        }
                    }
                    if (anyFresh) continue;
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                    // 忽略目录级错误
                }
            }
        }

        /// <summary>
        /// 保存地图渲染 PNG 缓存（无 WZ 时背景可离线显示）。key = mapId + crop 组合。
        /// </summary>
        /// <summary>保存世界地图层级树缓存（预渲染：Java familyMap/mapListCode 逻辑构建的 JSON，启动后台构建）。</summary>
        public void SaveMapHierarchy(string json)
        {
            try
            {
                var dir = Path.Combine(_cacheDir, "mapHierarchy");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "mapHierarchy.json"), json);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[CacheManager] SaveMapHierarchy: {ex.Message}"); }
        }

        /// <summary>读取世界地图层级树缓存；无缓存/损坏返回 null。</summary>
        public string? LoadMapHierarchy()
        {
            try
            {
                var p = Path.Combine(_cacheDir, "mapHierarchy", "mapHierarchy.json");
                if (!File.Exists(p)) return null;
                return File.ReadAllText(p);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[CacheManager] LoadMapHierarchy: {ex.Message}"); return null; }
        }

        public void SaveMapPng(string mapId, int cropX, int cropY, int cropW, int cropH, byte[] pngData)
        {
            if (string.IsNullOrEmpty(mapId) || pngData == null || pngData.Length == 0) return;
            lock (_lock)
            {
                try
                {
                    var path = GetMapPngPath(mapId, cropX, cropY, cropW, cropH);
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, pngData);
                }
                catch (IOException) { }
            }
        }

        /// <summary>
        /// 加载地图渲染 PNG 缓存；不存在返回 null。
        /// </summary>
        public byte[]? LoadMapPng(string mapId, int cropX, int cropY, int cropW, int cropH)
        {
            if (string.IsNullOrEmpty(mapId)) return null;
            lock (_lock)
            {
                try
                {
                    var path = GetMapPngPath(mapId, cropX, cropY, cropW, cropH);
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                }
                catch (IOException) { return null; }
            }
        }

        private string GetMapPngPath(string mapId, int cropX, int cropY, int cropW, int cropH) =>
            Path.Combine(_cacheDir, "maps", $"{mapId}_{cropX}_{cropY}_{cropW}_{cropH}.png");

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

        private string GetSpritePath(string mobId, string action, string extension)
        {
            return Path.Combine(_cacheDir, "sprites", mobId, $"{action}.{extension}");
        }

        private string GetBalloonPath(string balloonId)
        {
            return Path.Combine(_cacheDir, "balloons", balloonId);
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

        // ── 气泡切片 origin 持久化（MapleSalon2 拼装依赖）──

        /// <summary>气泡切片名（与 BalloonTiles 属性一一对应）</summary>
        private static readonly string[] TileNames = { "nw", "n", "ne", "w", "c", "e", "sw", "s", "se", "arrow", "head" };

        private static void SaveTileOrigins(string dir, BalloonTiles tiles)
        {
            try
            {
                var origins = new Dictionary<string, int[]>
                {
                    { "nw", new[] { tiles.NWOrigin.X, tiles.NWOrigin.Y } },
                    { "n", new[] { tiles.NOrigin.X, tiles.NOrigin.Y } },
                    { "ne", new[] { tiles.NEOrigin.X, tiles.NEOrigin.Y } },
                    { "w", new[] { tiles.WOrigin.X, tiles.WOrigin.Y } },
                    { "c", new[] { tiles.COrigin.X, tiles.COrigin.Y } },
                    { "e", new[] { tiles.EOrigin.X, tiles.EOrigin.Y } },
                    { "sw", new[] { tiles.SWOrigin.X, tiles.SWOrigin.Y } },
                    { "s", new[] { tiles.SOrigin.X, tiles.SOrigin.Y } },
                    { "se", new[] { tiles.SEOrigin.X, tiles.SEOrigin.Y } },
                    { "arrow", new[] { tiles.ArrowOrigin.X, tiles.ArrowOrigin.Y } },
                    { "head", new[] { tiles.HeadOrigin.X, tiles.HeadOrigin.Y } }
                };
                File.WriteAllText(Path.Combine(dir, "origin.json"), System.Text.Json.JsonSerializer.Serialize(origins));
            }
            catch (Exception ex) { Console.Error.WriteLine($"[CacheManager] SaveTileOrigins: {ex.Message}"); }
        }

        private static Dictionary<string, (int X, int Y)>? LoadTileOrigins(string dir)
        {
            try
            {
                var path = Path.Combine(dir, "origin.json");
                if (!File.Exists(path)) return null;
                var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(path));
                if (map == null) return null;
                var result = new Dictionary<string, (int X, int Y)>();
                foreach (var (k, v) in map)
                {
                    if (v != null && v.Length >= 2) result[k] = (v[0], v[1]);
                }
                return result;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[CacheManager] LoadTileOrigins: {ex.Message}"); return null; }
        }
    }
}
