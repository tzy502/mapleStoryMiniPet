using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;
using SkiaSharp;
using WzComparerR2.WzLib;

namespace MiniPet.Services;

public class MapService
{
    private readonly WzService _wz;
    private readonly CacheManager _cache;

    // 整图渲染缓存（ADR-0004）：桌面 crop 与设置预览共用一张整图，按 mapId 缓存；切图/退出 Dispose。
    private SKBitmap? _fullRender;
    private string? _fullRenderMapId;

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
        _fullRender?.Dispose();
        _fullRender = null;
        _fullRenderMapId = null;
    }

    // 摄像机模式（模式2）精灵缓存：wzPath → (bmp, originX, originY)。首次懒加载（ExtractPng+Decode+GetOrigin），
    // 后续帧命中缓存仅 DrawBitmap。地图切换时 ClearSpriteCache Dispose 清空。必须在 _wzLock 内访问（ExtractPng/GetOrigin 不自锁）。
    private readonly Dictionary<string, (SKBitmap? bmp, int ox, int oy)> _spriteCache = new();

    /// <summary>清空摄像机模式精灵缓存（地图切换/退出摄像机模式时调用，释放 SKBitmap 资源）。</summary>
    public void ClearSpriteCache()
    {
        foreach (var kv in _spriteCache) { try { kv.Value.bmp?.Dispose(); } catch { } }
        _spriteCache.Clear();
    }

    /// <summary>懒加载精灵（命中缓存直接返回；首次 ExtractPng+Decode+GetOrigin）。调用方须在 _wzLock 内。</summary>
    private (SKBitmap? bmp, int ox, int oy) GetCachedSprite(string wzPath)
    {
        if (string.IsNullOrEmpty(wzPath)) return (null, 0, 0);
        if (_spriteCache.TryGetValue(wzPath, out var entry)) return entry;
        SKBitmap? bmp = null; int ox = 0, oy = 0;
        var png = _wz.ExtractPng(wzPath);
        if (png != null && png.Length > 0)
        {
            bmp = SKBitmap.Decode(png);
            if (bmp != null)
            {
                var (ox2, oy2) = _wz.GetOrigin(wzPath);
                ox = ox2; oy = oy2;
            }
        }
        _spriteCache[wzPath] = (bmp, ox, oy);
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
            for (int i = 0; ; i++)
            {
                var tilePath = $"{layerPath}/tile/{i}";
                if (_wz.FindNodeByPath(tilePath) == null) break;
                var x = _wz.GetIntProperty($"{tilePath}/x");
                var y = _wz.GetIntProperty($"{tilePath}/y");
                var u = _wz.GetStringProperty($"{tilePath}/u") ?? "";
                var no = _wz.GetIntProperty($"{tilePath}/no");
                tiles.Add(new MapTile
                {
                    Id = i, X = x, Y = y,
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
            for (int i = 0; ; i++)
            {
                var objPath = $"{layerPath}/obj/{i}";
                if (_wz.FindNodeByPath(objPath) == null) break;
                var x = _wz.GetIntProperty($"{objPath}/x");
                var y = _wz.GetIntProperty($"{objPath}/y");
                var z = _wz.GetIntProperty($"{objPath}/z");
                var f = _wz.GetIntProperty($"{objPath}/f");
                var oS = _wz.GetStringProperty($"{objPath}/oS") ?? "";
                var l0 = _wz.GetStringProperty($"{objPath}/l0") ?? "";
                var l1 = _wz.GetStringProperty($"{objPath}/l1") ?? "";
                var l2 = _wz.GetStringProperty($"{objPath}/l2") ?? "";
                objs.Add(new MapObj
                {
                    Id = i, X = x, Y = y, Z = z, FlipX = f != 0,
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
                var alpha = _wz.GetIntProperty($"{bp}/a");
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
                    Resource = new Sprite { ResourceUrl = PickPortalFrameUrl(pt, image) ?? "" }
                });
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] ParsePortals: {ex.Message}"); }
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
    /// 在世界坐标 worldX 处的「地面」foothold 顶面 y（取包含该 x 的最高平台，即最小 y）。
    /// 用于把宠物脚底对齐到地面。找不到返回 null。
    /// </summary>
    public static int? GetGroundY(MapInfo mi, int worldX)
    {
        if (mi == null || mi.Footholds.Count == 0) return null;
        int? best = null;
        foreach (var fh in mi.Footholds)
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
    /// 全程在 WzService 锁内（WithWzLock 用 lock() 可重入 → 嵌套 RenderViewport 安全）。
    /// 模式1 不再用旧 RenderCore/DrawBack（back 视差 centerX 写死地图中心 + portal 顺序错），改为
    /// RenderViewport(map, camCenter=GetMapCenter, zoom=1, viewTimeMs=0) 的快照，与模式2 同一套 R2 渲染算法。
    /// 坐标空间：screenPx = world − MinX（camCenter=mapCenter, screenW=worldW 时 screen=(world-camCenter)+worldW/2=world-MinX），
    /// 与旧 RenderCore 一致 → CropFull 裁紧/EffectiveCrop 落地对齐逻辑不变。
    /// 返回缓存的 SKBitmap（调用方只读；需独立副本请用 CropFull 深拷贝）。
    /// </summary>
    private SKBitmap? RenderMapFull(MapInfo mapInfo)
    {
        if (mapInfo == null) return null;
        return _wz.WithWzLock(() =>
        {
            int worldW = mapInfo.MaxX - mapInfo.MinX;
            int worldH = mapInfo.MaxY - mapInfo.MinY;
            if (worldW <= 0 || worldH <= 0) return null;
            if (_fullRender != null && _fullRenderMapId == mapInfo.Id) return _fullRender;
            var (ccx, ccy) = GetMapCenter(mapInfo);
            var bmp = RenderViewport(mapInfo, ccx, ccy, 1f, 0, worldW, worldH);
            if (bmp == null) return null;
            _fullRender?.Dispose();
            _fullRender = bmp;
            _fullRenderMapId = mapInfo.Id;
            return _fullRender;
        });
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
        return _wz.WithWzLock(() =>
        {
            try
            {
                int worldW = mapInfo.MaxX - mapInfo.MinX;
                int worldH = mapInfo.MaxY - mapInfo.MinY;
                if (worldW <= 0 || worldH <= 0) return null;
                if (cropW <= 0 || cropH <= 0) { cropX = mapInfo.MinX; cropY = mapInfo.MinY; cropW = worldW; cropH = worldH; }
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
        });
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
        return _wz.WithWzLock(() =>
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
        });
    }

    /// <summary>对齐 R2 FrmMapRender.GetBackTileMode：WZ type 0-7 → TileMode 位（bit0=H,bit1=V,bit2=ScrollH 无水平视差,bit3=ScrollV）。</summary>
    private static int GetBackTileMode(int type) => type switch
    {
        0 => 0,
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 5,
        5 => 10,
        6 => 7,
        7 => 11,
        _ => 0
    };

    // ════════════════════════════════════════════════════════════════════════
    // 摄像机渲染（RenderViewport）— 模式1 与模式2 共用同一套 R2 算法
    // 算法 1:1 对齐 WzComparerR2.MapRender（MonoGame → SkiaSharp 移植）：
    //   相机：Origin = camCenter − screen/(2*zoom)；world→screen = (world−camCenter)*zoom + screen/2
    //   back 视差基于 camCenter（非 Origin），带符号 rx，整体 position 取 floor；portal 在 front-back 之前。
    // 模式1 静态 = RenderViewport(map, GetMapCenter, zoom=1, viewTimeMs=0) 的快照（RenderMapFull 调用）。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 渲染摄像机视口（模式2）。world→screen 变换后用缓存精灵绘制，back 视差/平铺按 GetMeshBack 公式。
    /// 渲染顺序对齐 MapScene.cs:13-19：Back(非Front) → Layer[0..7](Obj按Z升序→Tile) → Life → Portal → Front(IsFront back)。
    /// 关键：Portal 在 Front back 之前（对齐 Fly 阶段；模式1 RenderCore 把它放 front-back 之后是历史 bug，模式2 不重蹈）。
    /// back 视差用 camCenter（不是地图中心），无 (Camera.Height−600) Y 偏移（V2 已删）。
    /// 全程在 _wz.WithWzLock 内：首次懒加载 ExtractPng 需要锁；后续帧命中缓存仅 DrawBitmap（锁内串行，简单起见整帧持锁）。
    /// </summary>
    public SKBitmap? RenderViewport(MapInfo map, float camCenterX, float camCenterY, float zoom, long viewTimeMs, int screenW, int screenH)
    {
        if (map == null || screenW <= 0 || screenH <= 0 || zoom <= 0) return null;
        return _wz.WithWzLock(() =>
        {
            try
            {
                // 固定 BGRA8888/Premul：与 Avalonia WriteableBitmap(PixelFormat.Bgra8888, AlphaFormat.Premul) 一致，
                // 模式2 可 SKBitmap→WriteableBitmap 直拷像素（跳过 PNG 编码/解码，拖动跟手）。
                var result = new SKBitmap(screenW, screenH, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(result))
                {
                    canvas.Clear(SKColors.Transparent);
                    float halfW = screenW / 2f, halfH = screenH / 2f;

                    // 1. Back(非 IsFront) — back 视差基于 camCenter，跳过 spine(Ani==2) 与 screenMode!=0
                    if (MapShowBack)
                    {
                        foreach (var back in map.Backs)
                        {
                            if (back.Front || back.Ani == 2) continue;
                            DrawBackViewport(canvas, back, camCenterX, camCenterY, zoom, viewTimeMs, screenW, screenH);
                        }
                    }

                    // 2. 8 层 obj + tile：obj 按 Z 升序 thenBy Id，tile 覆盖 obj（对齐 renderMapLayers）
                    for (int i = 0; i < 8; i++)
                    {
                        var layer = map.Layers[i];
                        if (MapShowObj)
                        {
                            foreach (var obj in layer.Objs.OrderBy(o => o.Z).ThenBy(o => o.Id))
                            {
                                if (obj.Resource.Frames.Count == 0) continue;
                                DrawWorldSprite(canvas, obj.Resource.Frames[0].ResourceUrl, obj.X, obj.Y, obj.FlipX, camCenterX, camCenterY, zoom, halfW, halfH, 255);
                            }
                        }
                        if (MapShowTile)
                        {
                            foreach (var tile in layer.Tiles)
                                DrawWorldSprite(canvas, tile.Resource.ResourceUrl, tile.X, tile.Y, false, camCenterX, camCenterY, zoom, halfW, halfH, 255);
                        }
                    }

                    // 3. life — 定位 (X, Cy)（Cy 缺失回退 Y），flip，hide 跳过
                    if (MapShowLife)
                    {
                        foreach (var life in map.Lifes)
                        {
                            if (life.Hide || string.IsNullOrEmpty(life.Resource.ResourceUrl)) continue;
                            int ly = life.Cy != 0 ? life.Cy : life.Y;
                            DrawWorldSprite(canvas, life.Resource.ResourceUrl, life.X, ly, life.Flip, camCenterX, camCenterY, zoom, halfW, halfH, 255);
                        }
                    }

                    // 4. Portal — 必须在 front-back 之前（对齐 Fly 阶段）。定位 (X,Y)，无视差无 flip。
                    if (MapShowPortal)
                    {
                        foreach (var portal in map.Portals)
                        {
                            if (string.IsNullOrEmpty(portal.Resource.ResourceUrl)) continue;
                            DrawWorldSprite(canvas, portal.Resource.ResourceUrl, portal.X, portal.Y, false, camCenterX, camCenterY, zoom, halfW, halfH, 255);
                        }
                    }

                    // 5. Front(IsFront back) — 同 back 视差算法，最后绘制覆盖前景
                    if (MapShowBack)
                    {
                        foreach (var back in map.Backs)
                        {
                            if (!back.Front || back.Ani == 2) continue;
                            DrawBackViewport(canvas, back, camCenterX, camCenterY, zoom, viewTimeMs, screenW, screenH);
                        }
                    }

                    // 6. foothold 调试覆盖（默认关）
                    if (MapShowFoothold)
                    {
                        using var fhPaint = new SKPaint { Color = new SKColor(255, 0, 0, 180), IsAntialias = false, StrokeWidth = 2 };
                        foreach (var fh in map.Footholds)
                        {
                            float x1 = (fh.X1 - camCenterX) * zoom + halfW, y1 = (fh.Y1 - camCenterY) * zoom + halfH;
                            float x2 = (fh.X2 - camCenterX) * zoom + halfW, y2 = (fh.Y2 - camCenterY) * zoom + halfH;
                            canvas.DrawLine(x1, y1, x2, y2, fhPaint);
                        }
                    }
                }
                return result;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MapService] RenderViewport: {ex.Message}"); return null; }
        });
    }

    /// <summary>
    /// 绘制 world 坐标精灵（obj/tile/life/portal）：world→screen 后调 DrawSpriteScreen。
    /// screen = (world − camCenter) * zoom + (screenW/2, screenH/2)。
    /// </summary>
    private void DrawWorldSprite(SKCanvas canvas, string wzPath, int worldX, int worldY, bool flipX,
        float camCenterX, float camCenterY, float zoom, float halfW, float halfH, int alpha)
    {
        var (bmp, ox, oy) = GetCachedSprite(wzPath);
        if (bmp == null) return;
        float sx = (worldX - camCenterX) * zoom + halfW;
        float sy = (worldY - camCenterY) * zoom + halfH;
        using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
        if (alpha < 255) paint.Color = new SKColor(255, 255, 255, (byte)alpha);
        DrawSpriteScreen(canvas, bmp, ox, oy, sx, sy, flipX, zoom, paint);
    }

    /// <summary>
    /// 绘制 back（摄像机模式）— 视差/平铺对齐 FrmMapRender2.SceneRendering.cs:798-908 GetMeshBack：
    /// position = (back.X, back.Y)；水平 ScrollH: position.X += (rx*5*viewTimeMs/1000) % cx；否则视差 position.X += floor(camCenterX*(100+rx)/100)；
    /// 垂直同理。tileMode 映射沿用 GetBackTileMode（bit0 H, bit1 V, bit2 ScrollH, bit3 ScrollV）。
    /// 平铺（tileMode!=None）：world position→screen 后，tileScreenStep = cx*zoom（缩放下），覆盖 screenW×screenH（l/r/t/b 用 floor/ceil ±1）。
    /// 关键：带符号 rx，基于 camCenter（不是 Origin/地图中心），无 (Camera.Height−600) Y 偏移。screenMode!=0 跳过。
    /// </summary>
    private void DrawBackViewport(SKCanvas canvas, MapBack back, float camCenterX, float camCenterY, float zoom, long viewTimeMs, int screenW, int screenH)
    {
        if (back.ScreenMode != 0) return;
        var (bmp, ox, oy) = GetCachedSprite(back.Resource.ResourceUrl);
        if (bmp == null) return;

        int tm = GetBackTileMode(back.Type);
        int cx = back.Cx > 0 ? back.Cx : bmp.Width;
        int cy = back.Cy > 0 ? back.Cy : bmp.Height;

        // 视差偏移（world space）— 逐行对齐 R2 GetMeshBack(FrmMapRender2.SceneRendering.cs:830-859)
        float worldX = back.X;
        if ((tm & 4) != 0) { if (cx > 0) worldX += (back.Rx * 5f * viewTimeMs / 1000f) % cx; }
        else worldX += camCenterX * (100 + back.Rx) / 100f;
        float worldY = back.Y;
        if ((tm & 8) != 0) { if (cy > 0) worldY += (back.Ry * 5f * viewTimeMs / 1000f) % cy; }
        else worldY += camCenterY * (100 + back.Ry) / 100f;
        // R2: 对整体 position 取整（position.X = floor(position.X)），非对视差偏移单独取整。
        worldX = (float)Math.Floor(worldX);
        worldY = (float)Math.Floor(worldY);

        // world → screen
        float baseX = (worldX - camCenterX) * zoom + screenW / 2f;
        float baseY = (worldY - camCenterY) * zoom + screenH / 2f;

        int alpha = back.Alpha > 0 ? back.Alpha : 255;
        using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
        if (alpha < 255) paint.Color = new SKColor(255, 255, 255, (byte)alpha);

        bool tileH = (tm & 1) != 0, tileV = (tm & 2) != 0;
        if (!tileH && !tileV)
        {
            DrawSpriteScreen(canvas, bmp, ox, oy, baseX, baseY, back.FlipX, zoom, paint);
            return;
        }
        // 平铺覆盖屏幕：tileScreenStep = cx*zoom / cy*zoom
        float stepX = tileH ? cx * zoom : 0;
        float stepY = tileV ? cy * zoom : 0;
        int l = 0, r = 1, t = 0, b = 1;
        if (tileH)
        {
            l = (int)Math.Floor((0 - baseX) / stepX) - 1;
            r = (int)Math.Ceiling((screenW - baseX) / stepX) + 1;
        }
        if (tileV)
        {
            t = (int)Math.Floor((0 - baseY) / stepY) - 1;
            b = (int)Math.Ceiling((screenH - baseY) / stepY) + 1;
        }
        for (int iy = t; iy < b; iy++)
            for (int ix = l; ix < r; ix++)
                DrawSpriteScreen(canvas, bmp, ox, oy, baseX + ix * stepX, baseY + iy * stepY, back.FlipX, zoom, paint);
    }

    /// <summary>
    /// 在屏幕坐标 (screenX, screenY) 处绘制精灵（origin=(ox,oy) 为放置点）：
    /// translate→scale(flipX?−zoom:zoom, zoom)→drawBitmap(−ox,−oy)。绕放置点镜像 + 缩放。
    /// paint 由调用方创建/释放（平铺循环复用同一 paint）。
    /// </summary>
    private static void DrawSpriteScreen(SKCanvas canvas, SKBitmap bmp, int ox, int oy, float screenX, float screenY, bool flipX, float zoom, SKPaint paint)
    {
        canvas.Save();
        canvas.Translate(screenX, screenY);
        canvas.Scale(flipX ? -zoom : zoom, zoom);
        canvas.DrawBitmap(bmp, -ox, -oy, paint);
        canvas.Restore();
    }

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
