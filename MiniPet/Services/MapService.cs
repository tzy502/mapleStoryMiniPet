using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;
using SkiaSharp;
using WzComparerR2.WzLib;

namespace MiniPet.Services;

public partial class MapService
{
    private readonly WzService _wz;
    private readonly CacheManager _cache;

    // 整图渲染缓存（ADR-0004）：桌面 crop 与设置预览共用一张整图，按 mapId 缓存；切图/退出 Dispose。
    private SKBitmap? _fullRender;
    private string? _fullRenderCacheKey;

    // 分层显隐（默认与 PetSettings 一致：全开、Foothold 关）。RenderCore 按位掩码跳过对应层。
    // 调用方（PetWindow/Settings）可按需覆盖；不设置时使用默认值。
    public bool MapShowBack { get; set; } = true;
    public bool MapShowTile { get; set; } = true;
    public bool MapShowObj { get; set; } = true;
    public bool MapShowLife { get; set; } = true;
    public bool MapShowPortal { get; set; } = true;
    public bool MapShowFoothold { get; set; }

    public MapService(WzService wz, CacheManager cache)
    {
        _wz = wz;
        _cache = cache;
    }

    /// <summary>清除整图渲染缓存（分层显隐 toggle 后调用，强制下次按新 flag 重新渲染）。</summary>
    public void InvalidateFullRenderCache()
    {
        // M9：在 _spriteCacheLock 内 Dispose（与渲染串行，无在途引用）。
        // 原实现因渲染在 WZ 锁外跑而不敢 Dispose（后台 Task 可能持有引用 → 悬空指针），
        // M5 后整帧持 _spriteCacheLock，此处同锁内 Dispose 安全，内存立即释放。
        lock (_spriteCacheLock)
        {
            try { _fullRender?.Dispose(); } catch { }
            _fullRender = null;
            _fullRenderCacheKey = null;
        }
    }

    // 摄像机模式（模式2）精灵缓存：wzPath → (bmp, originX, originY)。首次懒加载（ExtractPng+Decode+GetOrigin），
    // 后续帧命中缓存仅 DrawBitmap。地图切换时 ClearSpriteCache Dispose 清空。
    // 锁（M1/M5）：整帧渲染持 _spriteCacheLock（轻量，不与 WZ 数据读取争用），ClearSpriteCache/InvalidateFullRenderCache 同锁
    // → 与在途渲染串行，杜绝「渲染中 Dispose 原生句柄」竞态（原实现整帧持 WZ 锁兜底，M5 收缩后需独立锁）。
    // 锁序：_spriteCacheLock → _wzLock（GetCachedSprite 未命中时在缓存锁内取 WZ 锁）；无反向路径，无死锁。
    // 上限保护：超过 MaxSpriteCacheEntries 时淘汰一半（大地图数千精灵时防 SKBitmap 常驻膨胀）。
    private const int MaxSpriteCacheEntries = 3000;
    private readonly object _spriteCacheLock = new();
    private readonly Dictionary<string, (SKBitmap? bmp, int ox, int oy)> _spriteCache = new();
    private readonly LinkedList<string> _spriteCacheOrder = new();
    // M12：LRU 键→链表节点字典，命中 O(1)（原 LinkedList.Remove 每帧数百次 O(n) 链表扫描）。
    private readonly Dictionary<string, LinkedListNode<string>> _spriteLruNodes = new();

    // 层排序缓存：key = "{mapId}|{layerIdx}|{showObj}|{showTile}" → 排序后的绘制项。
    // 地图不变时 (Z0,Z1,Id) 顺序固定，避免每帧 8 层 LINQ OrderBy 分配临时对象（P5-2）。
    private readonly Dictionary<string, List<(int Z0, int Z1, bool IsObj, MapObj? Obj, MapTile? Tile, MapLife? Life)>> _layerSortCache = new();

    // M11：SKPaint 提字段复用（原每精灵 using new SKPaint，大地图每帧数百次分配）。整帧渲染在 _spriteCacheLock 内串行，单实例安全。
    private readonly SKPaint _paintSprite = new() { IsAntialias = false, FilterQuality = SKFilterQuality.None };
    private readonly SKPaint _paintFoothold = new() { Color = new SKColor(255, 0, 0, 180), IsAntialias = false, StrokeWidth = 2 };

    /// <summary>清空摄像机模式精灵缓存（地图切换/退出摄像机模式/调用时，释放 SKBitmap 资源）。</summary>
    public void ClearSpriteCache()
    {
        // M1：收进 _spriteCacheLock —— RenderViewport 整帧持同一锁，在途帧先完成再 Dispose，
        // 消除「切图/停渲染时原生句柄与在途渲染并发释放」崩溃风险。
        lock (_spriteCacheLock)
        {
            foreach (var kv in _spriteCache) { try { kv.Value.bmp?.Dispose(); } catch { } }
            _spriteCache.Clear();
            _spriteCacheOrder.Clear();
            _spriteLruNodes.Clear();
            _layerSortCache.Clear();
        }
    }

    /// <summary>懒加载精灵（命中缓存直接返回；首次 ExtractPng+Decode+GetOrigin）。调用方须已在 _spriteCacheLock 内（RenderViewport 整帧持锁）。</summary>
    private (SKBitmap? bmp, int ox, int oy) GetCachedSprite(string wzPath)
    {
        if (string.IsNullOrEmpty(wzPath)) return (null, 0, 0);
        if (_spriteCache.TryGetValue(wzPath, out var entry))
        {
            // M12：命中 → 键→节点字典 O(1) 移到队尾（原 LinkedList.Remove(wzPath) 是 O(n) 扫描）
            if (_spriteLruNodes.TryGetValue(wzPath, out var node) && node.List == _spriteCacheOrder)
            {
                _spriteCacheOrder.Remove(node);
                _spriteCacheOrder.AddLast(node);
            }
            return entry;
        }
        // M5：WZ 锁收缩到精灵懒加载——仅未命中时的 ExtractPng/GetOrigin 持 WZ 锁（合并为一次锁区间），
        // PNG 解码在锁外（SKBitmap.Decode 线程安全）。负缓存（缺失 sprite 的 null 条目）随 ClearSpriteCache 清除（M14）。
        byte[]? png = null;
        var (ox, oy) = _wz.WithWzLock(() =>
        {
            png = _wz.ExtractPng(wzPath);
            return _wz.GetOrigin(wzPath);
        });
        SKBitmap? bmp = null;
        if (png != null && png.Length > 0) { bmp = SKBitmap.Decode(png); }
        _spriteCache[wzPath] = (bmp, ox, oy);
        _spriteLruNodes[wzPath] = _spriteCacheOrder.AddLast(wzPath);
        // 上限保护：超出时淘汰最久未用的一半（Dispose 释放 Skia 原生句柄）
        if (_spriteCache.Count > MaxSpriteCacheEntries)
        {
            int evictCount = _spriteCache.Count / 2;
            for (int i = 0; i < evictCount && _spriteCacheOrder.First != null; i++)
            {
                var oldest = _spriteCacheOrder.First;
                _spriteCacheOrder.RemoveFirst();
                _spriteLruNodes.Remove(oldest.Value);
                if (_spriteCache.TryGetValue(oldest.Value, out var ev))
                {
                    try { ev.bmp?.Dispose(); } catch { }
                    _spriteCache.Remove(oldest.Value);
                }
            }
        }
        return (bmp, ox, oy);
    }

    /// <summary>地图中心世界坐标（摄像机初始 camCenter）。VR/极值缺失时回退 0。</summary>
    public static (float cx, float cy) GetMapCenter(MapInfo? map)
        => map == null ? (0, 0) : ((map.MinX + map.MaxX) / 2f, (map.MinY + map.MaxY) / 2f);

    /// <summary>
    /// 相机 clamp（对齐 Camera.AdjustToWorldRect, Camera.cs:129-158）：
    /// camX ∈ [MinX + halfW, MaxX − halfW]，halfW = screenW/(2*zoom)；地图窄于视口（minX>maxX）时居中。Y 同理。
    /// </summary>
    public static (float camX, float camY) ClampCamera(MapInfo? map, float camX, float camY, float zoom, int screenW, int screenH)
    {
        if (map == null || zoom <= 0) return (camX, camY);
        float halfW = screenW / (2f * zoom);
        float halfH = screenH / (2f * zoom);
        float minX = map.MinX + halfW, maxX = map.MaxX - halfW;
        float cx = minX > maxX ? (map.MinX + map.MaxX) / 2f : Math.Clamp(camX, minX, maxX);
        float minY = map.MinY + halfH, maxY = map.MaxY - halfH;
        float cy = minY > maxY ? (map.MinY + map.MaxY) / 2f : Math.Clamp(camY, minY, maxY);
        return (cx, cy);
    }

    public MapInfo? LoadMap(string mapId) => _wz.WithWzLock(() => LoadMapCore(mapId));

    private MapInfo? LoadMapCore(string mapId)
    {
        try
        {
            var mi = new MapInfo { Id = mapId };
            var mapRoot = GetMapWzPath(mapId);

            // 各 layer 的 tile / obj
            for (int layerIdx = 0; layerIdx < 8; layerIdx++)
            {
                var layer = new MapLayer();
                var layerPath = $"{mapRoot}/{layerIdx}";

                if (_wz.FindNodeByPath($"{layerPath}/tile") != null)
                    layer.Tiles = ParseTiles(layerPath);
                if (_wz.FindNodeByPath($"{layerPath}/obj") != null)
                    layer.Objs = ParseObjs(layerPath);

                mi.Layers[layerIdx] = layer;
            }

            // back 层
            if (_wz.FindNodeByPath($"{mapRoot}/back") != null)
                mi.Backs = ParseBacks($"{mapRoot}/back");

            // foothold（地面/平台，用于角色落地对齐）
            if (_wz.FindNodeByPath($"{mapRoot}/foothold") != null)
                mi.Footholds = ParseFootholds($"{mapRoot}/foothold");

            // life（NPC/Mob）+ portal：在 mapRoot 下扁平解析（对齐 WzComparerR2 MapData.LoadLife/LoadPortal）
            if (_wz.FindNodeByPath($"{mapRoot}/life") != null)
                mi.Lifes = ParseLife($"{mapRoot}/life");
            if (_wz.FindNodeByPath($"{mapRoot}/portal") != null)
                mi.Portals = ParsePortals($"{mapRoot}/portal");
            // ladderRope（梯子/绳索，F18 行走攀爬用）
            if (_wz.FindNodeByPath($"{mapRoot}/ladderRope") != null)
                mi.Ropes = ParseLadderRopes($"{mapRoot}/ladderRope");

            // VR 视口边界（CalculateBounds 使用；缺失时回退到坐标极值）
            mi.VRLeft = _wz.GetIntProperty($"{mapRoot}/info/VRLeft");
            mi.VRTop = _wz.GetIntProperty($"{mapRoot}/info/VRTop");
            mi.VRRight = _wz.GetIntProperty($"{mapRoot}/info/VRRight");
            mi.VRBottom = _wz.GetIntProperty($"{mapRoot}/info/VRBottom");

            CalculateBounds(mi);
            return mi;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] LoadMap({mapId}): {ex.Message}"); return null; }
    }

    private void CalculateBounds(MapInfo mi)
    {
        try
        {
            // 1. VR 存在（非空）→ 直接用，对齐 WzComparerR2 MapData.LoadInfo/CalcMapSize（VRect 非空即返回）
            if (mi.VRRight > mi.VRLeft && mi.VRBottom > mi.VRTop)
            {
                mi.MinX = mi.VRLeft; mi.MinY = mi.VRTop;
                mi.MaxX = mi.VRRight; mi.MaxY = mi.VRBottom;
                return;
            }
            // 2. VR 缺失 → 用 foothold 矩形并集，对齐 CalcMapSize（仅 Y 方向加 padding：top-=250, height+=450）
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var fh in mi.Footholds)
            {
                int fxMin = Math.Min(fh.X1, fh.X2), fxMax = Math.Max(fh.X1, fh.X2);
                int fyMin = Math.Min(fh.Y1, fh.Y2), fyMax = Math.Max(fh.Y1, fh.Y2);
                if (fxMin < minX) minX = fxMin;
                if (fyMin < minY) minY = fyMin;
                if (fxMax > maxX) maxX = fxMax;
                if (fyMax > maxY) maxY = fyMax;
            }
            // 2b. 无 foothold → 回退 tile/obj 坐标极值（仍按 WzComparerR2 Y padding）
            if (minX == int.MaxValue)
            {
                foreach (var layer in mi.Layers)
                {
                    foreach (var t in layer.Tiles) { minX = Math.Min(minX, t.X); minY = Math.Min(minY, t.Y); maxX = Math.Max(maxX, t.X); maxY = Math.Max(maxY, t.Y); }
                    foreach (var o in layer.Objs) { minX = Math.Min(minX, o.X); minY = Math.Min(minY, o.Y); maxX = Math.Max(maxX, o.X); maxY = Math.Max(maxY, o.Y); }
                }
            }
            if (minX == int.MaxValue) { minX = 0; minY = 0; maxX = 800; maxY = 600; }
            // CalcMapSize: rect.Y -= 250; rect.Height += 450;（X 方向无 padding）
            mi.MinX = minX; mi.MinY = minY - 250;
            mi.MaxX = maxX; mi.MaxY = maxY - 250 + 450;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] CalculateBounds: {ex.Message}"); }
    }

    private List<MapTile> ParseTiles(string layerPath)
    {
        var tiles = new List<MapTile>();
        try
        {
            var tS = _wz.GetStringProperty($"{layerPath}/info/tS") ?? "";
            // 同 obj：tile 子节点名也不保证从 0 连续 → 遍历全部子节点，Id 取节点名（对齐 R2 TileItem）
            var tileRoot = _wz.FindNodeByPath($"{layerPath}/tile");
            if (tileRoot == null) { return tiles; }
            int tileSeq = 0;
            foreach (var tileNode in tileRoot.Nodes)
            {
                var tilePath = $"{layerPath}/tile/{tileNode.Text}";
                int tileId = int.TryParse(tileNode.Text, out var tidParsed) ? tidParsed : tileSeq;
                tileSeq++;
                var x = _wz.GetIntProperty($"{tilePath}/x");
                var y = _wz.GetIntProperty($"{tilePath}/y");
                var u = _wz.GetStringProperty($"{tilePath}/u") ?? "";
                var no = _wz.GetIntProperty($"{tilePath}/no");
                // M3：tile 排序 Z 取精灵帧自身 z（`Map/Tile/{tS}.img/{u}/{no}/z`）——
                // 对齐 R2 GetMeshTile（FrmMapRender2.SceneRendering.cs:943: mesh.Z0 = (renderObj as Frame)?.Z，
                // TextureLoader.cs:56: frame.Z = frameNode.z）与 PIXI（index.ts:574: zIndex = compositeZIndex(resource.z, id)）。
                // 原实现读地图节点 `tile/{i}/z`（该字段通常不存在 → 恒 0），同层 tile 顺序错乱、地面/装饰互盖错误。
                var tileFrameZ = _wz.GetIntProperty($"Map/Tile/{tS}.img/{u}/{no}/z");
                tiles.Add(new MapTile
                {
                    Id = tileId, X = x, Y = y,
                    Z = tileFrameZ,
                    Resource = new Sprite { ResourceUrl = $"Map/Tile/{tS}.img/{u}/{no}" }
                });
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] ParseTiles: {ex.Message}"); }
        return tiles;
    }

    private List<MapObj> ParseObjs(string layerPath)
    {
        var objs = new List<MapObj>();
        try
        {
            // ⚠️ 不能按 obj/0,1,2… 递增查找：WZ 里子节点名**不保证从 0 连续**
            //（实测 100000200 的层 0 是 "2","3",… 没有 "0" → 递增会第一次就 break，整层 obj 全丢，
            //  表现为「灌木/草丛缺失或位置不对」）。对齐参考 WzComparerR2 MapData.LoadObjects：
            //  foreach (var node in objNode.Nodes) + Index = int.Parse(node.Text)。
            var objRoot = _wz.FindNodeByPath($"{layerPath}/obj");
            if (objRoot == null) { return objs; }
            int objSeq = 0;
            foreach (var objNode in objRoot.Nodes)
            {
                var objPath = $"{layerPath}/obj/{objNode.Text}";
                int objId = int.TryParse(objNode.Text, out var nidParsed) ? nidParsed : objSeq;
                objSeq++;
                var x = _wz.GetIntProperty($"{objPath}/x");
                var y = _wz.GetIntProperty($"{objPath}/y");
                var z = _wz.GetIntProperty($"{objPath}/z");
                var f = _wz.GetIntProperty($"{objPath}/f");
                var oS = _wz.GetStringProperty($"{objPath}/oS") ?? "";
                var l0 = _wz.GetStringProperty($"{objPath}/l0") ?? "";
                var l1 = _wz.GetStringProperty($"{objPath}/l1") ?? "";
                var l2 = _wz.GetStringProperty($"{objPath}/l2") ?? "";
                // 忽略任务指引/教程引导类 obj（对齐 Java MapRenderHandler：effect.img/quest/ 与 guide.img/tutorial/）
                if (("effect".Equals(oS) && "quest".Equals(l0))
                    || ("guide".Equals(oS) && "tutorial".Equals(l0)))
                {
                    continue;
                }
                objs.Add(new MapObj
                {
                    Id = objId, X = x, Y = y, Z = z, FlipX = f != 0,
                    Resource = new FrameAnimate { Frames = new() { new Frame { ResourceUrl = $"Map/Obj/{oS}.img/{l0}/{l1}/{l2}/0" } } }
                });
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] ParseObjs: {ex.Message}"); }
        return objs;
    }

    private List<MapBack> ParseBacks(string backPath)
    {
        var backs = new List<MapBack>();
        try
        {
            for (int i = 0; ; i++)
            {
                var bp = $"{backPath}/{i}";
                if (_wz.FindNodeByPath(bp) == null) break;
                var bS = _wz.GetStringProperty($"{bp}/bS") ?? "";
                var no = _wz.GetIntProperty($"{bp}/no");
                var cx = _wz.GetIntProperty($"{bp}/cx");
                var cy = _wz.GetIntProperty($"{bp}/cy");
                var rx = _wz.GetIntProperty($"{bp}/rx");
                var ry = _wz.GetIntProperty($"{bp}/ry");
                // M13：alpha 默认 255（对齐 R2 BackItem.LoadFromNode: node.Nodes["a"].GetValueEx(255)）。
                // GetIntProperty 对缺失字段返回 0，无法区分「缺失=不透明」与「显式 a=0=不可见」，须查节点存在性。
                int alpha = 255;
                var aNode = _wz.FindNodeByPath($"{bp}/a");
                if (aNode != null) alpha = aNode.GetValueEx<int>(255);
                var front = _wz.GetIntProperty($"{bp}/front");
                var ani = _wz.GetIntProperty($"{bp}/ani");
                var type = _wz.GetIntProperty($"{bp}/type");
                var x = _wz.GetIntProperty($"{bp}/x");
                var y = _wz.GetIntProperty($"{bp}/y");
                var flip = _wz.GetIntProperty($"{bp}/f");
                var screenMode = _wz.GetIntProperty($"{bp}/screenMode");

                // aniDir：0→back(静态)，1→ani(目录取第 0 帧)，2→spine（跳过，无渲染器）
                string aniDir = ani switch { 0 => "back", 1 => "ani", _ => "spine" };
                string tex = ani == 1
                    ? $"Map/Back/{bS}.img/ani/{no}/0"
                    : $"Map/Back/{bS}.img/{aniDir}/{no}";

                backs.Add(new MapBack
                {
                    Id = i, X = x, Y = y, Cx = cx, Cy = cy, Rx = rx, Ry = ry,
                    Alpha = alpha, Front = front != 0, FlipX = flip != 0,
                    Ani = ani, Type = type, ScreenMode = screenMode,
                    Resource = new Sprite { ResourceUrl = tex }
                });
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] ParseBacks: {ex.Message}"); }
        return backs;
    }

    /// <summary>
    /// 解析 foothold 三层树 {mapRoot}/foothold/{l0}/{l1}/{l2}，每段 {x1,y1,x2,y2}。
    /// Layer = l0 层级索引（0=地面层，1+ = 平台/屋顶），供 GetGroundY 取地面（M7）。
    /// </summary>
    private List<MapFoothold> ParseFootholds(string fhPath)
    {
        var result = new List<MapFoothold>();
        try
        {
            var root = _wz.FindNodeByPath(fhPath);
            if (root == null) return result;
            foreach (Wz_Node l0 in root.Nodes)
                foreach (Wz_Node l1 in l0.Nodes)
                    foreach (Wz_Node l2 in l1.Nodes)
                    {
                        var p = $"{fhPath}/{l0.Text}/{l1.Text}/{l2.Text}";
                        result.Add(new MapFoothold
                        {
                            Id = int.TryParse(l2.Text, out var id) ? id : 0,
                            Layer = int.TryParse(l0.Text, out var layer) ? layer : 0,
                            X1 = _wz.GetIntProperty($"{p}/x1"),
                            Y1 = _wz.GetIntProperty($"{p}/y1"),
                            X2 = _wz.GetIntProperty($"{p}/x2"),
                            Y2 = _wz.GetIntProperty($"{p}/y2"),
                            Prev = _wz.GetIntProperty($"{p}/prev"),
                            Next = _wz.GetIntProperty($"{p}/next"),
                        });
                    }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] ParseFootholds: {ex.Message}"); }
        return result;
    }

    /// <summary>
    /// 解析 life（NPC/Mob）— 对齐 WzComparerR2 MapData.LoadLife / LifeItem.LoadFromNode。
    /// isCategory!=0 时为分组两层树；否则扁平。字段：id/type/x/y/cy/fh/f/hide。
    /// </summary>
    private List<MapLife> ParseLife(string lifePath)
    {
        var result = new List<MapLife>();
        try
        {
            var root = _wz.FindNodeByPath(lifePath);
            if (root == null) return result;
            bool isCategory = _wz.GetIntProperty($"{lifePath}/isCategory") != 0;
            if (!isCategory)
            {
                foreach (Wz_Node n in root.Nodes)
                    AddLife(result, $"{lifePath}/{n.Text}", n.Text);
            }
            else
            {
                foreach (Wz_Node group in root.Nodes)
                    foreach (Wz_Node n in group.Nodes)
                        AddLife(result, $"{lifePath}/{group.Text}/{n.Text}", n.Text);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] ParseLife: {ex.Message}"); }
        return result;
    }

    private void AddLife(List<MapLife> result, string p, string indexText)
    {
        try
        {
            var type = _wz.GetStringProperty($"{p}/type") ?? "n";
            // id：优先字符串（保留前导零），缺失回退 int
            var idStr = _wz.GetStringProperty($"{p}/id");
            if (string.IsNullOrEmpty(idStr)) idStr = _wz.GetIntProperty($"{p}/id").ToString();
            result.Add(new MapLife
            {
                Id = int.TryParse(indexText, out var idx) ? idx : 0,
                Type = type,
                X = _wz.GetIntProperty($"{p}/x"),
                Y = _wz.GetIntProperty($"{p}/y"),
                Cy = _wz.GetIntProperty($"{p}/cy"),
                Fh = _wz.GetIntProperty($"{p}/fh"),
                Z = _wz.GetIntProperty($"{p}/z"),
                Flip = _wz.GetIntProperty($"{p}/f") != 0,
                Hide = _wz.GetIntProperty($"{p}/hide") != 0,
                Resource = new Sprite { ResourceUrl = PickLifeFrameUrl(type, idStr) ?? "" }
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] AddLife({p}): {ex.Message}"); }
    }

    /// <summary>
    /// 取 life 首帧资源路径。对齐 WzComparerR2：State Animator 初始动作 = img 下首个非 info/condition 子节点，
    /// 取其帧 0。找不到则按 stand/idle/fly/move 回退（覆盖无 stand 的特殊 NPC/Mob）。
    /// 注意：必须先 Wz_Image.TryExtract() 才能枚举 .Node.Nodes（与 WzService.ExportSpriteStrip 同模式）。
    /// </summary>
    private string? PickLifeFrameUrl(string type, string idStr)
    {
        try
        {
            string top = type == "m" ? "Mob" : "Npc";
            string padded = (idStr ?? "").PadLeft(7, '0');
            string imgPath = $"{top}/{padded}.img";
            var imgNode = GetExtractedImgNode(imgPath);
            if (imgNode != null)
            {
                foreach (Wz_Node child in imgNode.Nodes)
                {
                    var name = child.Text;
                    if (name == "info" || name.StartsWith("condition") || int.TryParse(name, out _)) continue;
                    if (child.FindNodeByPath("0") != null) return $"{imgPath}/{name}/0";
                }
            }
            return null;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] PickLifeFrameUrl({type}/{idStr}): {ex.Message}"); return null; }
    }

    /// <summary>解析到已 TryExtract 的 img 节点树（返回 img.Node），非 img 节点原样返回。调用方须在 _wzLock 内。</summary>
    private Wz_Node? GetExtractedImgNode(string imgPath)
    {
        var node = _wz.FindNodeByPath(imgPath);
        if (node == null) return null;
        var img = node.GetValue<Wz_Image>();
        if (img == null) return node; // 非 img 节点（已展开的目录）
        return img.TryExtract() ? img.Node : null;
    }

    /// <summary>
    /// 解析 portal — 对齐 WzComparerR2 MapData.LoadPortal / PortalItem.LoadFromNode。
    /// 字段：pn/pt/x/y/tm/tn/image。资源取 game 动画帧 0（Map/MapHelper.img/portal/game/{type}/{img}）。
    /// </summary>
    private List<MapPortal> ParsePortals(string portalPath)
    {
        var result = new List<MapPortal>();
        try
        {
            for (int i = 0; ; i++)
            {
                var p = $"{portalPath}/{i}";
                if (_wz.FindNodeByPath(p) == null) break;
                var pt = _wz.GetIntProperty($"{p}/pt");
                var image = _wz.GetIntProperty($"{p}/image");
                result.Add(new MapPortal
                {
                    Id = i,
                    Type = pt,
                    Image = image,
                    X = _wz.GetIntProperty($"{p}/x"),
                    Y = _wz.GetIntProperty($"{p}/y"),
                    PName = _wz.GetStringProperty($"{p}/pn") ?? "",
                    Tm = _wz.GetStringProperty($"{p}/tm") ?? "",
                    Tn = _wz.GetStringProperty($"{p}/tn") ?? "",
                    Resource = new Sprite { ResourceUrl = PickPortalFrameUrl(pt, image) ?? "" }
                });
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] ParsePortals: {ex.Message}"); }
        return result;
    }

    /// <summary>
    /// 解析梯子 / 绳索（F18 行走攀爬）—— WZ `ladderRope/{i}`：x / y1 / y2 / l。
    /// </summary>
    private List<LadderRope> ParseLadderRopes(string ropePath)
    {
        var result = new List<LadderRope>();
        try
        {
            // ⚠️ ladderRope 的子节点**不是从 0 连续编号**（probe 实测 101000000 有 14 个子节点但无 "0"），
            // 必须直接遍历节点，不能按 {ropePath}/{i} 递增查找（否则第一次就 break → 0 根）。
            var root = _wz.FindNodeByPath(ropePath);
            if (root == null) { return result; }
            foreach (var n in root.Nodes)
            {
                var p = $"{ropePath}/{n.Text}";
                result.Add(new LadderRope
                {
                    Id = int.TryParse(n.Text, out var rid) ? rid : 0,
                    X = _wz.GetIntProperty($"{p}/x"),
                    Y1 = _wz.GetIntProperty($"{p}/y1"),
                    Y2 = _wz.GetIntProperty($"{p}/y2"),
                    IsLadder = _wz.GetIntProperty($"{p}/l") != 0,
                });
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] ParseLadderRopes: {ex.Message}"); }
        return result;
    }

    // PortalTypes[pt]：对齐 WzComparerR2 PortalItem.PortalTypes，索引 = WZ 字段 pt。
    private static readonly string[] PortalTypes =
        { "sp", "pi", "pv", "pc", "pg", "tp", "ps", "pgi", "psi", "pcs", "ph", "psh", "pcj", "pci", "pci2", "pcig", "pshg", "pcc", "pcir" };

    /// <summary>
    /// 取 portal game 动画可见帧 — 逐行对齐 R2 MapData.PreloadResource(MapData.cs:787-847) + GetMeshPortal(FrmMapRender2.SceneRendering.cs:964-977)。
    /// R2 game 模式(IsEditorMode=false 默认)：精确单路径 game/{PortalTypes[pt]}/{imgName}，animator==null 则 GetMeshPortal 返回 null，portal 不画。
    /// **无 editor 占位回退** — editor/ 资源仅 R2 编辑模式(Ctrl+8)使用；game 模式下回退会画出 R2 不渲染的「多余传送门」(粉色图钉)。
    /// type 7(pgi) 复用 PortalTypes[2]=pv（R2 switch 一致）；image=0 → "default"，否则 image.ToString()。
    /// 帧选择：帧动画→0 帧；状态机(portalStart/portalContinue/portalExit)→portalContinue/0(正常可见光圈)，次选 portalStart/0。
    /// Map/MapHelper.img 是独立 img，必须先 TryExtract 才能遍历子树。
    /// </summary>
    private string? PickPortalFrameUrl(int pt, int image)
    {
        try
        {
            string typeName = (pt >= 0 && pt < PortalTypes.Length) ? PortalTypes[pt] : PortalTypes[0];
            if (pt == 7) typeName = PortalTypes[2]; // pgi(7) → pv（R2 MapData.cs:808-812）
            string imgName = image == 0 ? "default" : image.ToString(); // R2 MapData.cs:814-820
            var helper = GetExtractedImgNode("Map/MapHelper.img");
            if (helper == null) return null;
            var dir = helper.FindNodeByPath(true, "portal", "game", typeName, imgName);
            if (dir == null) return null; // R2 game 模式：资源不存在 → animator=null → portal 不画（不回退 editor）
            if (dir.FindNodeByPath("0") != null) return $"Map/MapHelper.img/portal/game/{typeName}/{imgName}/0";
            if (dir.FindNodeByPath("portalContinue")?.FindNodeByPath("0") != null) return $"Map/MapHelper.img/portal/game/{typeName}/{imgName}/portalContinue/0";
            if (dir.FindNodeByPath("portalStart")?.FindNodeByPath("0") != null) return $"Map/MapHelper.img/portal/game/{typeName}/{imgName}/portalStart/0";
            return null;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] PickPortalFrameUrl({pt}/{image}): {ex.Message}"); return null; }
    }

    public static string GetMapWzPath(string mapId)
    {
        var prefix = !string.IsNullOrEmpty(mapId) ? mapId[0] : '0';
        return $"Map/Map/Map{prefix}/{mapId}.img";
    }

    /// <summary>
    /// 在世界坐标 worldX 处的「地面」foothold 顶面 y。
    /// M7：地面 = foothold 树第 0 层（WZ `foothold/{l0}/{l1}/{l2}` 的 l0，0=地面层，1+ = 平台/屋顶）。
    /// 原实现取「覆盖该 x 的最高平台（最小 y）」→ 宠物站上屋顶/树顶。
    /// 修正：仅在地图层（l0==0）内取最高点；地面层存在但该 x 无覆盖 → 返回 null（调用方回退窗口底部，
    /// 绝不回退到平台层——那正是「站屋顶」原 bug）。地图完全没有 l0==0 地面层时才回退全量最高点（保证仍能落地）。
    /// </summary>
    public static int? GetGroundY(MapInfo mi, int worldX)
    {
        if (mi == null || mi.Footholds.Count == 0) return null;
        var groundLayer = mi.Footholds.Where(f => f.Layer == 0).ToList();
        return PickHighestFootholdY(groundLayer.Count > 0 ? groundLayer : mi.Footholds, worldX);
    }

    private static int? PickHighestFootholdY(List<MapFoothold> fhs, int worldX)
    {
        int? best = null;
        foreach (var fh in fhs)
        {
            int xMin = Math.Min(fh.X1, fh.X2), xMax = Math.Max(fh.X1, fh.X2);
            if (worldX < xMin || worldX > xMax) continue;
            int y = (fh.X1 == fh.X2) ? fh.Y1
                : fh.Y1 + (fh.X2 == fh.X1 ? 0 : (int)((long)(worldX - fh.X1) * (fh.Y2 - fh.Y1) / (fh.X2 - fh.X1)));
            if (best == null || y < best.Value) best = y;
        }
        return best;
    }

    /// <summary>
    /// 渲染整张地图（模式1 静态 = 摄像机第0帧）并按 mapId 缓存（ADR-0004）。桌面 crop 与设置预览共用此整图。
    /// M5：不再整帧持 WZ 锁（渲染/WZ 读取已各自加锁）；改持 _spriteCacheLock——与换图 Dispose（M9）、
    /// ClearSpriteCache（M1）、InvalidateFullRenderCache 串行，保证返回位图在使用期间不会被并发释放。
    /// 模式1 不再用旧 RenderCore/DrawBack（back 视差 centerX 写死地图中心 + portal 顺序错），改为
    /// RenderViewport(map, camCenter=GetMapCenter, zoom=1, viewTimeMs=0) 的快照，与模式2 同一套 R2 渲染算法。
    /// 坐标空间：screenPx = world − MinX（camCenter=mapCenter, screenW=worldW 时 screen=(world-camCenter)+worldW/2=world-MinX），
    /// 与旧 RenderCore 一致 → CropFull 裁紧/EffectiveCrop 落地对齐逻辑不变。
    /// 返回缓存的 SKBitmap（调用方只读；需独立副本请用 CropFull 深拷贝）。
    /// </summary>
    private SKBitmap? RenderMapFull(MapInfo mapInfo)
    {
        if (mapInfo == null) return null;
        lock (_spriteCacheLock)
        {
            int worldW = mapInfo.MaxX - mapInfo.MinX;
            int worldH = mapInfo.MaxY - mapInfo.MinY;
            if (worldW <= 0 || worldH <= 0) return null;
            // 缓存键 = mapId + 分层 flag（修复：切地图后背景/NPC/传送门重新出现，
            // 根因是缓存只按 mapId 命中，flag 变化前渲染的旧图被复用，胶水反馈 2026-08-09）
            string cacheKey = $"{mapInfo.Id}|{MapShowBack}|{MapShowTile}|{MapShowObj}|{MapShowLife}|{MapShowPortal}|{MapShowFoothold}";
            if (_fullRender != null && _fullRenderCacheKey == cacheKey) return _fullRender;
            var (ccx, ccy) = GetMapCenter(mapInfo);
            var bmp = RenderViewport(mapInfo, ccx, ccy, 1f, 0, worldW, worldH);
            if (bmp == null) return null;
            // M9：换图/换 flag 时旧整图在锁内 Dispose（M5 后整帧持 _spriteCacheLock，无在途引用竞态），
            // 快速连续切图不再出现原生内存膨胀（原实现只置 null 等 GC）。
            try { _fullRender?.Dispose(); } catch { }
            _fullRender = bmp;
            _fullRenderCacheKey = cacheKey;
            return _fullRender;
        }
    }

    /// <summary>
    /// 从整图渲染取 crop 子区为 PNG（深拷贝，ADR-0004 / F3），并裁紧四边透明 padding（#6）。
    /// N1：cw<=0 归一化为整图（"无 crop=整图"唯一规则点）；F5：源矩形与 [0,worldW]×[0,worldH] 求交，越界留透明、尺寸不变。
    /// #6：blit 完成后扫描 alpha 通道，裁掉四边全透明行/列（裁紧到实际非透明内容 bbox），返回裁紧后的 PNG。
    ///     WorldX/WorldY = 返回图左上角对应的「世界坐标」（= 输入 cropX/cropY + 裁掉的左/上透明像素数），
    ///     调用方（BackgroundWindow）据此修正窗口落地坐标，使宠物落地对齐（PlacePetOnMapGround）仍正确。
    /// </summary>
    public CroppedMap? CropFull(MapInfo mapInfo, int cropX, int cropY, int cropW, int cropH)
    {
        if (mapInfo == null) return null;
        // M5：不再整段持 WZ 锁；改持 _spriteCacheLock，与整图缓存换图 Dispose（M9）/ClearSpriteCache（M1）串行，
        // 防止从 _fullRender blit 期间位图被并发释放（原 WZ 锁语义下 InvalidateFullRenderCache 只置 null，M9 改为 Dispose 后必须同锁）。
        lock (_spriteCacheLock)
        {
            try
            {
                int worldW = mapInfo.MaxX - mapInfo.MinX;
                int worldH = mapInfo.MaxY - mapInfo.MinY;
                if (worldW <= 0 || worldH <= 0) return null;
                if (cropW <= 0 || cropH <= 0) { cropX = mapInfo.MinX; cropY = mapInfo.MinY; cropW = worldW; cropH = worldH; }
                // M15：超大 crop 上限（防单张 RGBA 位图内存失控；4096×4096 ≈ 67MB 为上限）
                const int MaxCropDim = 4096;
                if (cropW > MaxCropDim || cropH > MaxCropDim)
                {
                    Console.Error.WriteLine($"[MapService] CropFull: crop {cropW}×{cropH} 超上限 {MaxCropDim}×{MaxCropDim}，已收缩");
                    cropW = Math.Min(cropW, MaxCropDim);
                    cropH = Math.Min(cropH, MaxCropDim);
                }
                var full = RenderMapFull(mapInfo);
                if (full == null) return null;

                // 固定 RGBA8888 以便 alpha 扫描（alpha 在每像素字节 3）；Skia 内部处理源图色域转换。
                using var dst = new SKBitmap(cropW, cropH, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(dst))
                {
                    canvas.Clear(SKColors.Transparent);
                    int srcX = cropX - mapInfo.MinX;
                    int srcY = cropY - mapInfo.MinY;
                    int x0 = Math.Max(0, srcX), y0 = Math.Max(0, srcY);
                    int x1 = Math.Min(worldW, srcX + cropW), y1 = Math.Min(worldH, srcY + cropH);
                    if (x1 > x0 && y1 > y0)
                    {
                        using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
                        canvas.DrawBitmap(full, new SKRect(x0, y0, x1, y1), new SKRect(x0 - srcX, y0 - srcY, x1 - srcX, y1 - srcY), paint);
                    }
                }

                // #6: 扫描 alpha 找实际内容 bbox，裁紧四边全透明 padding。
                var (trimL, trimT, trimR, trimB) = FindContentBBox(dst);
                int contentW = trimR - trimL;
                int contentH = trimB - trimT;
                if (contentW <= 0 || contentH <= 0) return null; // 全透明：无可见内容

                byte[] png;
                if (trimL == 0 && trimT == 0 && contentW == cropW && contentH == cropH)
                {
                    using var img0 = SKImage.FromBitmap(dst);
                    png = img0.Encode(SKEncodedImageFormat.Png, 100).ToArray();
                }
                else
                {
                    using var sub = new SKBitmap(contentW, contentH, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using (var c = new SKCanvas(sub))
                    {
                        c.Clear(SKColors.Transparent);
                        using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
                        c.DrawBitmap(dst, new SKRect(trimL, trimT, trimR, trimB), new SKRect(0, 0, contentW, contentH), paint);
                    }
                    using var img = SKImage.FromBitmap(sub);
                    png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
                }
                // 返回图左上角对应的世界坐标：原 crop 左上 + 裁掉的透明偏移。
                return new CroppedMap(png, cropX + trimL, cropY + trimT, contentW, contentH);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MapService] CropFull: {ex.Message}"); return null; }
        }
    }

    /// <summary>
    /// 扫描位图 alpha 通道，返回实际非透明内容的 bbox（左/上/右/下，半开区间）。
    /// 用于 #6 裁紧四边全透明 padding。alpha==0 视为透明（仅裁真正空白边缘，不误伤抗锯齿淡边）。
    /// 仅在 RGBA8888 位图上调用（CropFull 固定以此色域创建 dst）。
    /// </summary>
    private static (int l, int t, int r, int b) FindContentBBox(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w <= 0 || h <= 0) return (0, 0, 0, 0);
        int left = w, top = h, right = 0, bottom = 0;
        var span = bmp.GetPixelSpan();
        int rowBytes = bmp.RowBytes;
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                if (span[rowBase + x * 4 + 3] != 0) // RGBA8888: alpha 在字节 3
                {
                    if (x < left) left = x;
                    if (x + 1 > right) right = x + 1;
                    if (y < top) top = y;
                    if (y + 1 > bottom) bottom = y + 1;
                }
            }
        }
        if (right <= left || bottom <= top) return (0, 0, 0, 0);
        return (left, top, right, bottom);
    }

    /// <summary>
    /// 渲染地图缩略 PNG（设置中心预览用）：消费 RenderMapFull 后缩放，与桌面 crop 同源。
    /// </summary>
    public byte[]? CompositeMapThumbnail(MapInfo mapInfo, int targetW, int targetH)
    {
        if (mapInfo == null || targetW <= 0 || targetH <= 0) return null;
        // M5：同 CropFull——改持 _spriteCacheLock（与整图缓存换图 Dispose 串行），不再整段持 WZ 锁。
        lock (_spriteCacheLock)
        {
            try
            {
                int worldW = mapInfo.MaxX - mapInfo.MinX;
                int worldH = mapInfo.MaxY - mapInfo.MinY;
                if (worldW <= 0 || worldH <= 0) return null;
                var full = RenderMapFull(mapInfo);
                if (full == null) return null;
                double scale = Math.Min((double)targetW / worldW, (double)targetH / worldH);
                int sw = Math.Max(1, (int)(worldW * scale));
                int sh = Math.Max(1, (int)(worldH * scale));
                using var scaled = new SKBitmap(sw, sh);
                using (var canvas = new SKCanvas(scaled))
                {
                    canvas.Clear(SKColors.Transparent);
                    using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
                    canvas.DrawBitmap(full, new SKRect(0, 0, sw, sh), paint);
                }
                using var img = SKImage.FromBitmap(scaled);
                return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MapService] CompositeMapThumbnail: {ex.Message}"); return null; }
        }
    }

    /// <summary>对齐 R2 FrmMapRender.GetBackTileMode：WZ type 0-7 → TileMode 位（bit0=H,bit1=V,bit2=ScrollH 无水平视差,bit3=ScrollV）。</summary>
    public List<(string id, string name)> GetMapList()
    {
        return new List<(string, string)>
        {
            ("100000000", "射手村"),
            ("101000000", "魔法密林"),
            ("102000000", "废弃都市"),
            ("103000000", "天空之城"),
            ("240010500", "神木村")
        };
    }
}

/// <summary>
/// CropFull 裁紧后的结果（#6）：Png 为裁去四边透明 padding 后的位图，
/// WorldX/WorldY 为该图左上角对应的「地图世界坐标」，Width/Height 为实际像素尺寸。
/// BackgroundWindow 据此把窗口尺寸设为实际可见内容尺寸，并暴露 EffectiveCrop 供宠物落地对齐使用。
/// </summary>
public sealed class CroppedMap
{
    public byte[] Png { get; }
    public int WorldX { get; }
    public int WorldY { get; }
    public int Width { get; }
    public int Height { get; }
    public CroppedMap(byte[] png, int worldX, int worldY, int width, int height)
    {
        Png = png; WorldX = worldX; WorldY = worldY; Width = width; Height = height;
    }
}
