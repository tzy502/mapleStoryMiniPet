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
        private CacheManager? _cache;
        // 地图目录缓存（id→显示名）：后台构建、单飞、版本化
        private readonly object _mapCatalogLock = new();
        private Dictionary<string, string>? _mapNameCache;
        // 区域→街道→地图 三级层级（F6 级联筛选）：随目录后台构建，WZ 重载时置空重建
        private List<MapRegionNode>? _mapHierarchy;
        // 世界地图树（树状选择器）：构建后缓存到磁盘（mapHierarchy.json），启动直接读缓存
        private List<WorldMapTreeNode>? _worldMapTree;
        private Dictionary<string, string>? _mapStreetInfo;   // mapId → "street|mapName"
        private int _mapCatalogGen;
        private Task? _mapCatalogTask;

        public MapCatalogService(WzService wz, CacheManager? cache = null)
        {
            _wz = wz;
            _cache = cache;
        }

        /// <summary>启动后注入缓存（世界地图树预渲染缓存用）。</summary>
        public void SetCache(CacheManager cache) => _cache = cache;

        /// <summary>WZ 重载时清空目录缓存（由 WzService.LoadWz 失败/重载路径调用）。</summary>
        public void Reset()
        {
            lock (_mapCatalogLock)
            {
                _mapNameCache = null;
                _mapHierarchy = null;
                // M8：Reset 全清——原实现漏清 _worldMapTree/_mapStreetInfo，换 WZ 后树选择器/街道信息仍显示旧数据。
                _worldMapTree = null;
                _mapStreetInfo = null;
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
        /// 构建「区域 → 街道 → 地图」三级层级（F6 级联筛选）。
        /// 区域 = Map/WorldMap 世界区域的面包屑（参考 MapleMapServiceImpl.intiWikiMap L262 与数据库 family_map：
        /// 父链 parentMap 递归 + 自己名，如「冒险岛世界 / 金银岛 / 林中之城」）；
        /// 地图归属 = WorldMap MapList/{i}/mapNo/0（探针实证 Int32 地图代码，与数据库 map_list_code 同源）；
        /// 街道 = 区域内地图的 streetName（中文，原逻辑）。持 _wzLock。
        /// </summary>
        public List<MapRegionNode> BuildMapHierarchy()
        {
            var regions = new List<MapRegionNode>();
            if (!_wz.IsWzLoaded || _wz.WzRoot == null) return regions;
            lock (_wz.WzLock)
            {
                try
                {
                    // 1. String/Map.img 全量地图：id → (streetName, mapName)
                    var mapInfo = new Dictionary<string, (string Street, string Name)>();
                    var imgNode = _wz.WzRoot.FindNodeByPath(true, "String", "Map.img");
                    var img = imgNode?.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract()) return regions;
                    foreach (Wz_Node group in img.Node.Nodes)
                    {
                        foreach (Wz_Node idNode in group.Nodes)
                        {
                            var id = idNode.Text;
                            if (!int.TryParse(id, out _)) continue;
                            var resolved = idNode.ResolveUol() ?? idNode;
                            var mapName = resolved.FindNodeByPath("mapName")?.GetValueEx<string>(null);
                            var streetName = resolved.FindNodeByPath("streetName")?.GetValueEx<string>(null);
                            mapInfo[id] = (streetName ?? "(未分类街道)", mapName ?? id);
                        }
                    }

                    // 2. WorldMap 区域：key → (name, parentMap, 地图代码列表)
                    var wm = new Dictionary<string, (string Name, string Parent, List<string> MapIds)>();
                    var wmFile = _wz.WzRoot.FindNodeByPath("Map", true)?.Nodes.FirstOrDefault(n => n.Text == "WorldMap");
                    var swmNode = _wz.WzRoot.FindNodeByPath(true, "String", "WorldMap.img");
                    var swm = swmNode?.GetValue<Wz_Image>();
                    swm?.TryExtract();
                    if (wmFile != null)
                    {
                        foreach (Wz_Node n in wmFile.Nodes)
                        {
                            if (!n.Text.EndsWith(".img")) continue;
                            var key = n.Text[..^4];
                            if (!key.StartsWith("WorldMap")) continue;
                            key = key["WorldMap".Length..];
                            if (key.Length == 0 || key.All(char.IsLetter)) continue; // 总览图/字母键
                            var wmImg = n.GetValue<Wz_Image>();
                            if (wmImg == null || !wmImg.TryExtract()) continue;
                            var parent = wmImg.Node.FindNodeByPath("info")?.FindNodeByPath("parentMap")?.GetValueEx<string>(null);
                            var name = swm?.Node.FindNodeByPath(key)?.FindNodeByPath("name")?.GetValueEx<string>(null);
                            // MapList/{i}/mapNo/0 = 地图代码（Int32）
                            var mapIds = new List<string>();
                            var mapList = wmImg.Node.FindNodeByPath("MapList");
                            if (mapList != null)
                            {
                                foreach (Wz_Node spot in mapList.Nodes)
                                {
                                    var code = spot.FindNodeByPath("mapNo")?.Nodes.FirstOrDefault()?.GetValueEx<int?>(null);
                                    if (code != null && code.Value > 0) mapIds.Add(code.Value.ToString());
                                }
                            }
                            wm[key] = (name ?? key, parent ?? "", mapIds);
                        }
                    }
                    // 面包屑：父链（跳过 WorldMap/EGWorldMap 等非区域父）+ 自己名（Java intiWikiMap L272-282 逻辑）
                    string Crumb(string key, int depth)
                    {
                        if (depth > 8 || !wm.TryGetValue(key, out var v)) return key;
                        if (string.IsNullOrEmpty(v.Parent) || v.Parent == "WorldMap" || v.Parent == key) return v.Name;
                        var pk = v.Parent.StartsWith("WorldMap") ? v.Parent["WorldMap".Length..] : v.Parent;
                        var p = Crumb(pk, depth + 1);
                        return p == v.Name ? v.Name : p + " / " + v.Name;
                    }

                    // 3. 区域 → 街道分组（MapList 精确归属；未归属地图归「其他」）
                    var otherMaps = new List<MapEntry>();
                    var regionOrder = new List<string>();
                    var regionStreets = new Dictionary<string, List<(string Street, List<MapEntry> Maps)>>();
                    foreach (var kv in wm)
                    {
                        if (kv.Value.MapIds.Count == 0) continue;
                        if (!regionOrder.Contains(kv.Key)) regionOrder.Add(kv.Key);
                        if (!regionStreets.TryGetValue(kv.Key, out var sl)) { sl = new List<(string, List<MapEntry>)>(); regionStreets[kv.Key] = sl; }
                        var byStreet = new Dictionary<string, List<MapEntry>>();
                        foreach (var mid in kv.Value.MapIds)
                        {
                            if (!mapInfo.TryGetValue(mid, out var mi)) continue;
                            if (!byStreet.TryGetValue(mi.Street, out var list)) { list = new List<MapEntry>(); byStreet[mi.Street] = list; }
                            list.Add(new MapEntry(mid, mi.Name));
                        }
                        foreach (var s in byStreet)
                        {
                            s.Value.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
                            sl.Add((s.Key, s.Value));
                        }
                    }
                    // 未归属地图（不在任何 MapList）→ 「其他」
                    var allListed = new HashSet<string>(wm.Values.SelectMany(v => v.MapIds));
                    foreach (var kv in mapInfo.OrderBy(x => x.Key))
                    {
                        if (!allListed.Contains(kv.Key)) otherMaps.Add(new MapEntry(kv.Key, kv.Value.Name));
                    }
                    if (otherMaps.Count > 0)
                    {
                        if (!regionOrder.Contains("")) regionOrder.Add("");
                        if (!regionStreets.TryGetValue("", out var osl)) { osl = new List<(string, List<MapEntry>)>(); regionStreets[""] = osl; }
                        osl.Add(("其他", otherMaps));
                    }
                    // 按区域顺序生成（顶级在前：父先于子，保 WZ 序）
                    foreach (var rk in regionOrder)
                    {
                        var name = string.IsNullOrEmpty(rk) ? "其他" : Crumb(rk, 0);
                        var region = new MapRegionNode { Name = name };
                        foreach (var (street, maps) in regionStreets[rk].OrderBy(x => x.Street, StringComparer.Ordinal))
                        {
                            region.Streets.Add(new MapStreetNode { Name = street, Maps = maps });
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

        /// <summary>
        /// 世界地图树（Java familyMap/mapListCode 逻辑）：Map/WorldMap/{key}.img 的 parentMap 建树 +
        /// MapList/mapNo/0 地图归属 + String/WorldMap.img 名称。构建后写本地缓存（mapHierarchy.json），
        /// 下次启动直接读缓存（免实时 WZ 解析，胶水规则 2026-08-08）。
        /// M16/D2：磁盘缓存读取移入 _mapCatalogLock 内 + 二次检查——原实现锁外读盘 + 锁内构建，
        /// 双线程可重复读盘/重复构建（各持锁数秒）。
        /// M8：缓存文件名带 WZ 路径指纹——换 WZ（不同目录/版本）后指纹变化 → 不命中旧缓存，自动重建。
        /// </summary>
        public List<WorldMapTreeNode> GetWorldMapTree()
        {
            var cached = _worldMapTree;
            if (cached != null) return cached;
            lock (_mapCatalogLock)
            {
                // M16/D2：锁内二次检查（首次检查后到加锁期间其他线程可能已构建完成）
                if (_worldMapTree != null) { return _worldMapTree; }
                var fp = GetWzCacheFingerprint();
                if (_cache != null && fp != null)
                {
                    var json = _cache.LoadMapHierarchy(fp);
                    if (!string.IsNullOrEmpty(json))
                    {
                        try
                        {
                            var loaded = System.Text.Json.JsonSerializer.Deserialize<List<WorldMapTreeNode>>(json);
                            if (loaded != null && loaded.Count > 0)
                            {
                                _worldMapTree = loaded;
                                Console.WriteLine($"[MapCatalogService] 世界地图树从缓存加载: {loaded.Count} 顶级区域 (fp={fp})");
                                return loaded;
                            }
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[MapCatalogService] 树缓存反序列化失败: {ex.Message}"); }
                    }
                }
                var built = BuildWorldMapTree();
                if (built.Count == 0) return built;
                _worldMapTree = built;
                if (_cache != null && fp != null)
                {
                    try
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(built);
                        _cache.SaveMapHierarchy(fp, json);
                        Console.WriteLine($"[MapCatalogService] 世界地图树构建完成并缓存: {built.Count} 顶级区域");
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[MapCatalogService] 树缓存写入失败: {ex.Message}"); }
                }
                return _worldMapTree;
            }
        }

        /// <summary>
        /// WZ 指纹：已加载 WZ 文件的路径集合（排序后哈希）——换 WZ 目录/版本 → 指纹变化 → 磁盘缓存失效（M8）。
        /// 取不到结构时返回 null（调用方跳过磁盘缓存，安全降级为每次重建）。
        /// </summary>
        private string? GetWzCacheFingerprint()
        {
            try
            {
                var root = _wz.WzRoot;
                if (root == null) { return null; }
                var entry = root.GetValue<Wz_File>();
                var structure = entry?.WzStructure;
                if (structure == null) { return null; }
                var names = structure.wz_files
                    .Select(f => { try { return f.FileStream?.Name; } catch { return null; } })
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
                if (names.Count == 0) { return null; }
                var joined = string.Join("|", names);
                var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
                return Convert.ToHexString(hash).Substring(0, 16);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MapCatalogService] GetWzCacheFingerprint: {ex.Message}"); return null; }
        }

        /// <summary>mapId → "街道|地图名"（树选中节点 → 街道/地图列表）。构建失败返回空字典。</summary>
        public Dictionary<string, string> GetMapStreetInfo()
        {
            var cached = _mapStreetInfo;
            if (cached != null) return cached;
            var info = new Dictionary<string, string>();
            if (!_wz.IsWzLoaded || _wz.WzRoot == null) return info;
            lock (_wz.WzLock)
            {
                try
                {
                    var imgNode = _wz.WzRoot.FindNodeByPath(true, "String", "Map.img");
                    var img = imgNode?.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract()) return info;
                    foreach (Wz_Node group in img.Node.Nodes)
                    {
                        foreach (Wz_Node idNode in group.Nodes)
                        {
                            var id = idNode.Text;
                            if (!int.TryParse(id, out _)) continue;
                            var resolved = idNode.ResolveUol() ?? idNode;
                            var mapName = resolved.FindNodeByPath("mapName")?.GetValueEx<string>(null);
                            var streetName = resolved.FindNodeByPath("streetName")?.GetValueEx<string>(null);
                            info[id] = (streetName ?? "(未分类街道)") + "|" + (mapName ?? id);
                        }
                    }
                    _mapStreetInfo = info;
                }
                catch (Exception ex) { Console.Error.WriteLine($"[MapCatalogService] GetMapStreetInfo: {ex.Message}"); }
            }
            return info;
        }

        /// <summary>城镇判定：Map.wz Map/{id}.img/info/town == 1。持 _wzLock。</summary>
        private bool IsTownMap(string mapId)
        {
            try
            {
                if (string.IsNullOrEmpty(mapId) || !_wz.IsWzLoaded || _wz.WzRoot == null) return false;
                lock (_wz.WzLock)
                {
                    var n = _wz.WzRoot.FindNodeByPath(true, $"Map/Map/Map{mapId[0]}/{mapId}.img".Split('/'));
                    var img = n?.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract()) return false;
                    var town = img.Node.FindNodeByPath("info")?.FindNodeByPath("town")?.GetValueEx<int?>(null);
                    return town == 1;
                }
            }
            catch { return false; }
        }

        /// <summary>构建叶子节点（名称 = 街道:地图名）。</summary>
        private static WorldMapTreeNode MakeLeaf(string id, Dictionary<string, string> info, bool isTown = false)
        {
            string name;
            if (info.TryGetValue(id, out var v))
            {
                var parts = v.Split('|');
                name = parts.Length > 1 && !string.IsNullOrEmpty(parts[0]) && parts[0] != "(未分类街道)"
                    ? parts[0] + ":" + parts[1] : parts[1];
            }
            else { name = id; }
            return new WorldMapTreeNode { IsLeafMap = true, LeafMapId = id, Key = id, Name = name, IsTown = isTown };
        }

        /// <summary>
        /// 区域节点自身地图叶子（仅 MapList，不含 portal 关联/MapLink）：供「有子区域的区域」展开时显示自身挂载地图。
        /// 顺序：城镇（字符串从小到大）→ 非城镇（字符串从小到大），胶水规则 2026-08-09。
        /// </summary>
        public List<WorldMapTreeNode> LoadSelfMaps(WorldMapTreeNode node)
        {
            var maps = new List<WorldMapTreeNode>();
            if (node == null) return maps;
            var info = GetMapStreetInfo();
            var towns = new List<WorldMapTreeNode>();
            var others = new List<WorldMapTreeNode>();
            foreach (var id in node.MapIds.OrderBy(x => x, StringComparer.Ordinal))
            {
                var leaf = MakeLeaf(id, info, IsTownMap(id));
                if (leaf.IsTown) towns.Add(leaf); else others.Add(leaf);
            }
            maps.AddRange(towns);
            maps.AddRange(others);
            return maps;
        }

        /// <summary>
        /// 懒加载最底层区域的叶子地图：MapLink toolTip 叶子 + MapList 地图 + 这些地图的 portal 传送门关联地图。
        /// 胶水规则 2026-08-08：点击展开时才渲染，避免低性能；名称 = 街道:地图名（Java MapleMapServiceImpl L285）。
        /// </summary>
        public List<WorldMapTreeNode> LoadLeafMaps(WorldMapTreeNode node)
        {
            var maps = new List<WorldMapTreeNode>();
            if (node == null) return maps;
            var ids = new HashSet<string>(node.MapIds);
            var info = GetMapStreetInfo();
            if (_wz.IsWzLoaded && _wz.WzRoot != null)
            {
                lock (_wz.WzLock)
                {
                    try
                    {
                        // MapLink toolTip 叶子（WZ MapLink/{i}/toolTip = 链接名）
                        var wmFile = _wz.WzRoot.FindNodeByPath("Map", true)?.Nodes.FirstOrDefault(n => n.Text == "WorldMap");
                        var wmImg = wmFile?.Nodes.FirstOrDefault(x => x.Text == "WorldMap" + node.Key + ".img")?.GetValue<Wz_Image>();
                        wmImg?.TryExtract();
                        var mapLink = wmImg?.Node.FindNodeByPath("MapLink");
                        if (mapLink != null)
                        {
                            foreach (Wz_Node spot in mapLink.Nodes)
                            {
                                var toolTip = spot.FindNodeByPath("toolTip")?.GetValueEx<string>(null);
                                if (!string.IsNullOrEmpty(toolTip))
                                {
                                    maps.Add(new WorldMapTreeNode { IsLeafMap = true, LeafMapId = toolTip, Key = toolTip, Name = toolTip });
                                }
                            }
                        }
                        // MapList 地图的 portal 传送门关联
                        foreach (var mid in node.MapIds)
                        {
                            if (mid.Length == 0) continue;
                            var imgNode = _wz.WzRoot.FindNodeByPath(true, $"Map/Map/Map{mid[0]}/{mid}.img".Split('/'));
                            var img = imgNode?.GetValue<Wz_Image>();
                            if (img == null || !img.TryExtract()) continue;
                            var portal = img.Node.FindNodeByPath("portal");
                            if (portal == null) continue;
                            foreach (Wz_Node p in portal.Nodes)
                            {
                                var to = p.FindNodeByPath("to")?.GetValueEx<int?>(null);
                                if (to != null && to.Value > 0) ids.Add(to.Value.ToString());
                            }
                        }
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[MapCatalogService] LoadLeafMaps: {ex.Message}"); }
                }
            }
            var towns = new List<WorldMapTreeNode>();
            var others = new List<WorldMapTreeNode>();
            foreach (var id in ids.OrderBy(x => x, StringComparer.Ordinal))
            {
                var leaf = MakeLeaf(id, info, IsTownMap(id));
                if (leaf.IsTown) towns.Add(leaf); else others.Add(leaf);
            }
            maps.AddRange(towns);
            maps.AddRange(others);
            return maps;
        }

        /// <summary>从 WZ 构建世界地图树（Java familyMap/mapListCode 逻辑同源，一切从 WZ）。
        /// 根 = 世界节点（WorldMap 冒险岛世界 / GWorldMap / MWorldMap / SWorldMap 等，无 parentMap；
        /// EGWorldMap/CGWorldMap 挂 GWorldMap）；区域按 parentMap 挂到世界/父区域下。
        /// 名字全取 WZ：区域 = String/WorldMap.img；世界 = WorldMap.img 用 String "0"（冒险岛世界），
        /// 其余世界 WZ 无名（string 为空）→ 显示 key（不硬编码）。</summary>
        private List<WorldMapTreeNode> BuildWorldMapTree()
        {
            var roots = new List<WorldMapTreeNode>();
            if (!_wz.IsWzLoaded || _wz.WzRoot == null) return roots;
            lock (_wz.WzLock)
            {
                try
                {
                    var wmFile = _wz.WzRoot.FindNodeByPath("Map", true)?.Nodes.FirstOrDefault(n => n.Text == "WorldMap");
                    var swmNode = _wz.WzRoot.FindNodeByPath(true, "String", "WorldMap.img");
                    var swm = swmNode?.GetValue<Wz_Image>();
                    swm?.TryExtract();
                    if (wmFile == null) return roots;
                    var nodes = new Dictionary<string, WorldMapTreeNode>();
                    var parents = new Dictionary<string, string>();
                    foreach (Wz_Node n in wmFile.Nodes)
                    {
                        if (!n.Text.EndsWith(".img")) continue;
                        var rawKey = n.Text[..^4];
                        if (!rawKey.Contains("WorldMap")) continue;
                        // 数字区域：WorldMap{数字}（余部非纯字母）→ 去前缀；世界节点（WorldMap 自身/GWorldMap/MWorldMap/CGWorldMap/EGWorldMap/SWorldMap/WGWorldMap/WorldMapCN）保持原名
                        bool isWorld;
                        string key;
                        if (rawKey.StartsWith("WorldMap") && rawKey.Length > "WorldMap".Length && !rawKey["WorldMap".Length..].All(char.IsLetter))
                        {
                            key = rawKey["WorldMap".Length..];
                            isWorld = false;
                        }
                        else
                        {
                            key = rawKey;
                            isWorld = true;
                        }
                        // WorldMap0.img（key="0"）是旧版总览副本（String/WorldMap.img/0 与 WorldMap.img 同名）→ 跳过，避免重复「冒险岛世界」根
                        if (!isWorld && key == "0") continue;
                        if (isWorld)
                        {
                            // 世界节点（键 = 原名：WorldMap/GWorldMap/MWorldMap/EGWorldMap/CGWorldMap/SWorldMap/WGWorldMap/WorldMapCN）
                            var full = key;
                            var wmImg = n.GetValue<Wz_Image>();
                            if (wmImg == null || !wmImg.TryExtract()) continue;
                            var parent = wmImg.Node.FindNodeByPath("info")?.FindNodeByPath("parentMap")?.GetValueEx<string>(null);
                            // 世界名：String/WorldMap.img/{strKey}/name（冒险岛世界键="0"；GWorldMap 等字母世界也查 String——胶水纠正 2026-08-09：本来有名字，不覆盖）
                            var strKey = full == "WorldMap" ? "0" : full;
                            var name = swm?.Node.FindNodeByPath(strKey)?.FindNodeByPath("name")?.GetValueEx<string>(null) ?? full;
                            // 世界节点也读 MapList
                            var mapIds = new List<string>();
                            var mapList = wmImg.Node.FindNodeByPath("MapList");
                            if (mapList != null)
                            {
                                foreach (Wz_Node spot in mapList.Nodes)
                                {
                                    var code = spot.FindNodeByPath("mapNo")?.Nodes.FirstOrDefault()?.GetValueEx<int?>(null);
                                    if (code != null && code.Value > 0) mapIds.Add(code.Value.ToString());
                                }
                            }
                            nodes[full] = new WorldMapTreeNode { Key = full, Name = name, MapIds = mapIds };
                            parents[full] = parent ?? "";
                            continue;
                        }
                        // 区域节点（数字 key）
                        var wmImg2 = n.GetValue<Wz_Image>();
                        if (wmImg2 == null || !wmImg2.TryExtract()) continue;
                        var parent2 = wmImg2.Node.FindNodeByPath("info")?.FindNodeByPath("parentMap")?.GetValueEx<string>(null);
                        var name2 = swm?.Node.FindNodeByPath(key)?.FindNodeByPath("name")?.GetValueEx<string>(null);
                        var mapIds2 = new List<string>();
                        var mapList2 = wmImg2.Node.FindNodeByPath("MapList");
                        if (mapList2 != null)
                        {
                            foreach (Wz_Node spot in mapList2.Nodes)
                            {
                                var code = spot.FindNodeByPath("mapNo")?.Nodes.FirstOrDefault()?.GetValueEx<int?>(null);
                                if (code != null && code.Value > 0) mapIds2.Add(code.Value.ToString());
                            }
                        }
                        nodes[key] = new WorldMapTreeNode { Key = key, Name = name2 ?? key, MapIds = mapIds2 };
                        parents[key] = parent2 ?? "";
                    }
                    // 建树：父 = parentMap（"WorldMap010"→节点键"010"；"WorldMap"→"WorldMap"；"GWorldMap"→"GWorldMap"）
                    foreach (var kv in nodes)
                    {
                        var pk = parents[kv.Key];
                        if (pk.StartsWith("WorldMap"))
                        {
                            var rest = pk["WorldMap".Length..];
                            if (rest.Length > 0 && !rest.All(char.IsLetter)) pk = rest; // 数字余部去前缀；WorldMap 自身/字母世界保留
                        }
                        if (!string.IsNullOrEmpty(pk) && pk != kv.Key && nodes.TryGetValue(pk, out var parent))
                        {
                            parent.Children.Add(kv.Value);
                        }
                        else
                        {
                            roots.Add(kv.Value);
                        }
                    }
                    // TotalCount 递归
                    int Total(WorldMapTreeNode node)
                    {
                        int c = node.MapIds.Count;
                        foreach (var ch in node.Children) c += Total(ch);
                        node.TotalCount = c;
                        return c;
                    }
                    foreach (var r in roots) Total(r);
                    // 通用逻辑：字母世界节点（key 全字母）的 MapLink toolTip 按序补充子节点名（String 缺名显示 key 时；不硬编码任何世界——胶水需求 2026-08-09）
                    foreach (var root in roots)
                    {
                        if (root.Key.Length == 0 || !root.Key.All(char.IsLetter) || root.Children.Count == 0) continue;
                        // 修复：img 在 WorldMap 节点下（原 _wz.WzRoot.FindNodeByPath("Map").Nodes 找不到——自测实证 2026-08-09）
                        var wmImgNode = wmFile.Nodes.FirstOrDefault(x => x.Text == root.Key + ".img");
                        var wmImg = wmImgNode?.GetValue<Wz_Image>();
                        wmImg?.TryExtract();
                        var tips = wmImg?.Node.FindNodeByPath("MapLink")?.Nodes
                            .Select(s => s.FindNodeByPath("toolTip")?.GetValueEx<string>(null))
                            .Where(t => !string.IsNullOrEmpty(t))
                            .ToList();
                        if (tips == null || tips.Count == 0) continue;
                        int idx = 0;
                        foreach (var child in root.Children)
                        {
                            // 子节点名仍是 key（String 无此区域名）时，用父世界 MapLink 的 toolTip 按序补充
                            if (child.Name == child.Key && idx < tips.Count)
                            {
                                child.Name = tips[idx];
                            }
                            idx++;
                        }
                        // 自测日志：世界子节点名（胶水要求 2026-08-09）
                        Console.WriteLine($"[MapCatalogService] 世界 {root.Key} 子节点: {string.Join(", ", root.Children.Select(c => c.Name))}");
                    }
                    // 最底层区域（无子区域且有 MapList 地图）→ 挂占位子节点（展开时懒加载叶子）
                    foreach (var node in nodes.Values)
                    {
                        if (node.Children.Count == 0 && node.MapIds.Count > 0)
                        {
                            node.Children.Add(new WorldMapTreeNode { IsLeafPlaceholder = true, Name = "加载地图…" });
                        }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[MapCatalogService] BuildWorldMapTree: {ex.Message}"); }
            }
            return roots;
        }
    }
}
