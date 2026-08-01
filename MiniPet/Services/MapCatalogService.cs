using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MiniPet.Models;
using WzComparerR2.WzLib;

namespace MiniPet.Services
{
    /// <summary>
    /// 地图目录服务 — 从 WzService 拆出（God Class 拆分）：地图 id 枚举、id→显示名 缓存、
    /// 区域→街道→地图 三级层级。组合持有 WzService（经 internal 访问器取根节点与锁），
    /// 行为与原 WzService 内嵌实现完全一致。
    /// </summary>
    public class MapCatalogService
    {
        private readonly WzService _wz;
        // 地图目录缓存（id→显示名）：后台构建、单飞、版本化
        private readonly object _mapCatalogLock = new();
        private Dictionary<string, string>? _mapNameCache;
        // 区域→街道→地图 三级层级（F6 级联筛选）：随目录后台构建，WZ 重载时置空重建
        private List<MapRegionNode>? _mapHierarchy;
        private int _mapCatalogGen;
        private Task? _mapCatalogTask;

        public MapCatalogService(WzService wz)
        {
            _wz = wz;
        }

        /// <summary>WZ 重载时清空目录缓存（由 WzService.LoadWz 失败/重载路径调用）。</summary>
        public void Reset()
        {
            lock (_mapCatalogLock)
            {
                _mapNameCache = null;
                _mapHierarchy = null;
                _mapCatalogTask = null;
                _mapCatalogGen = 0;
            }
        }

        /// <summary>
        /// 枚举 WZ 全量地图 id：遍历 Map/Map/Map{0..9} 下所有 .img，文件名去 .img = 9 位地图 id。持 _wzLock。
        /// </summary>
        public List<string> ListMapIds()
        {
            var result = new List<string>();
            if (!_wz.IsWzLoaded || _wz.WzRoot == null) return result;
            lock (_wz.WzLock)
            {
                try
                {
                    var mapMapNode = _wz.WzRoot.FindNodeByPath(true, "Map", "Map");
                    if (mapMapNode == null) return result;
                    var ids = new HashSet<string>();
                    for (int g = 0; g <= 9; g++)
                    {
                        var groupNode = mapMapNode.FindNodeByPath($"Map{g}");
                        if (groupNode == null) continue;
                        foreach (Wz_Node child in groupNode.Nodes)
                        {
                            var name = child.Text;
                            if (!name.EndsWith(".img")) continue;
                            var id = name[..^4];
                            if (int.TryParse(id, out _)) ids.Add(id);
                        }
                    }
                    result.AddRange(ids);
                    result.Sort(StringComparer.Ordinal);
                }
                catch (Exception ex) { Console.Error.WriteLine($"[MapCatalogService] ListMapIds: {ex.Message}"); }
            }
            return result;
        }

        /// <summary>
        /// 构建 id→显示名 字典：遍历 String/Map.img/{地区分组}/{id}，id 节点 ResolveUol 后读 mapName+streetName，
        /// 显示名 = streetName：mapName；缺失回退 map_{id}。持 _wzLock。
        /// </summary>
        public Dictionary<string, string> BuildMapNameCache()
        {
            var cache = new Dictionary<string, string>();
            if (!_wz.IsWzLoaded || _wz.WzRoot == null) return cache;
            lock (_wz.WzLock)
            {
                try
                {
                    var imgNode = _wz.WzRoot.FindNodeByPath(true, "String", "Map.img");
                    var img = imgNode?.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract()) return cache;
                    foreach (Wz_Node group in img.Node.Nodes)
                    {
                        foreach (Wz_Node idNode in group.Nodes)
                        {
                            var id = idNode.Text;
                            if (!int.TryParse(id, out _)) continue;
                            var resolved = idNode.ResolveUol() ?? idNode;
                            var mapName = resolved.FindNodeByPath("mapName")?.GetValueEx<string>(null);
                            var streetName = resolved.FindNodeByPath("streetName")?.GetValueEx<string>(null);
                            string display;
                            if (!string.IsNullOrEmpty(streetName) && !string.IsNullOrEmpty(mapName))
                                display = $"{streetName}：{mapName}";
                            else if (!string.IsNullOrEmpty(mapName)) display = mapName;
                            else if (!string.IsNullOrEmpty(streetName)) display = streetName;
                            else display = $"map_{id}";
                            cache[id] = display;
                        }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[MapCatalogService] BuildMapNameCache: {ex.Message}"); }
            }
            return cache;
        }

        /// <summary>等待地图目录缓存就绪（轮询，超时返回当前态）。供 MapBrowserWindow 后台消费。</summary>
        public bool WaitForMapCatalog(int timeoutMs = 10000)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (_mapNameCache != null && _mapNameCache.Count > 0) return true;
                System.Threading.Thread.Sleep(100);
            }
            return _mapNameCache != null && _mapNameCache.Count > 0;
        }

        /// <summary>取地图显示名（查目录缓存，未命中回退 map_{id}）。</summary>
        public string GetMapName(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return string.Empty;
            var cache = _mapNameCache;
            if (cache != null && cache.TryGetValue(mapId, out var name)) return name;
            return $"map_{mapId}";
        }

        /// <summary>
        /// 后台构建地图目录缓存（单飞 + 版本化）：LoadWz 成功后触发；WZ 重载时仅发布最新一次构建结果。
        /// </summary>
        public void EnsureMapCatalog()
        {
            if (!_wz.IsWzLoaded) return;
            lock (_mapCatalogLock)
            {
                if (_mapNameCache != null && _mapNameCache.Count > 0) return;
                if (_mapCatalogTask != null && !_mapCatalogTask.IsCompleted) return;
                _mapCatalogGen++;
                var gen = _mapCatalogGen;
                _mapCatalogTask = Task.Run(() =>
                {
                    var built = BuildMapNameCache();
                    lock (_mapCatalogLock)
                    {
                        if (gen == _mapCatalogGen)
                        {
                            _mapNameCache = built;
                            Console.WriteLine($"[MapCatalogService] 地图目录构建完成: {built.Count} 个");
                        }
                    }
                });
            }
        }

        /// <summary>
        /// 构建 String/Map.img 的「区域 → 街道 → 地图」三级层级（F6 级联筛选）：
        /// 顶层分组节点 = 区域(group.Text)，其下 id 节点的 streetName = 街道、mapName+id = 地图。
        /// 与 BuildMapNameCache 同源遍历，但保留分组结构。持 _wzLock。
        /// </summary>
        public List<MapRegionNode> BuildMapHierarchy()
        {
            var regions = new List<MapRegionNode>();
            if (!_wz.IsWzLoaded || _wz.WzRoot == null) return regions;
            lock (_wz.WzLock)
            {
                try
                {
                    var imgNode = _wz.WzRoot.FindNodeByPath(true, "String", "Map.img");
                    var img = imgNode?.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract()) return regions;
                    // 保持 WZ 原始区域顺序，仅在区域内部对街道/地图排序
                    foreach (Wz_Node group in img.Node.Nodes)
                    {
                        var regionName = group.Text;
                        if (string.IsNullOrEmpty(regionName)) continue;
                        Dictionary<string, List<MapEntry>>? streets = null;
                        foreach (Wz_Node idNode in group.Nodes)
                        {
                            var id = idNode.Text;
                            if (!int.TryParse(id, out _)) continue;
                            var resolved = idNode.ResolveUol() ?? idNode;
                            var mapName = resolved.FindNodeByPath("mapName")?.GetValueEx<string>(null);
                            var streetName = resolved.FindNodeByPath("streetName")?.GetValueEx<string>(null);
                            var streetKey = !string.IsNullOrEmpty(streetName) ? streetName : "(未分类街道)";
                            var display = !string.IsNullOrEmpty(mapName) ? mapName
                                          : (!string.IsNullOrEmpty(streetName) ? streetName : $"map_{id}");
                            streets ??= new Dictionary<string, List<MapEntry>>();
                            if (!streets.TryGetValue(streetKey, out var list)) { list = new List<MapEntry>(); streets[streetKey] = list; }
                            list.Add(new MapEntry(id, display));
                        }
                        if (streets == null) continue;
                        var region = new MapRegionNode { Name = regionName };
                        foreach (var kv in streets.OrderBy(x => x.Key, StringComparer.Ordinal))
                        {
                            kv.Value.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
                            region.Streets.Add(new MapStreetNode { Name = kv.Key, Maps = kv.Value });
                        }
                        regions.Add(region);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[MapCatalogService] BuildMapHierarchy: {ex.Message}"); }
            }
            return regions;
        }

        /// <summary>
        /// 取区域→街道→地图 三级层级：目录已就绪则用缓存，否则现场构建并缓存（单飞由 _mapCatalogLock 保护）。
        /// 供背景设置 tab 的级联下拉消费。
        /// </summary>
        public List<MapRegionNode> GetMapHierarchy()
        {
            var cached = _mapHierarchy;
            if (cached != null) return cached;
            var built = BuildMapHierarchy();
            if (built.Count == 0) return built;
            lock (_mapCatalogLock)
            {
                if (_mapHierarchy == null) _mapHierarchy = built;
                return _mapHierarchy;
            }
        }
    }
}
