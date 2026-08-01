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
    /// 取 portal game 动画帧 0。type 7 复用 pv（对齐 WzComparerR2）；image=0 → "default"。
    /// game 动画优先 0 帧，状态机（portalStart/portalContinue）次之，最后 editor 占位回退。
    /// Map/MapHelper.img 是独立 img，必须先 TryExtract 才能遍历子树。
    /// </summary>
    private string? PickPortalFrameUrl(int pt, int image)
    {
        try
        {
            string typeName = (pt >= 0 && pt < PortalTypes.Length) ? PortalTypes[pt] : PortalTypes[0];
            if (pt == 7) typeName = PortalTypes[2]; // pv
            var helper = GetExtractedImgNode("Map/MapHelper.img");
            if (helper == null) return null;
            // type 候选：映射值 → 回退 pv（本 WZ game/ 仅 pv/ph/psh/ps，pi/psi 等缺资源）
            // imgName 候选：image==0→default/0/1；否则 image/default
            var typeNames = new[] { typeName, "pv" };
            var imgNames = image == 0 ? new[] { "default", "0", "1" } : new[] { image.ToString(), "default" };
            foreach (var tn in typeNames)
            {
                foreach (var im in imgNames)
                {
                    var dir = helper.FindNodeByPath(true, "portal", "game", tn, im);
                    if (dir == null) continue;
                    if (dir.FindNodeByPath("0") != null) return $"Map/MapHelper.img/portal/game/{tn}/{im}/0";
                    if (dir.FindNodeByPath("portalContinue")?.FindNodeByPath("0") != null) return $"Map/MapHelper.img/portal/game/{tn}/{im}/portalContinue/0";
                    if (dir.FindNodeByPath("portalStart")?.FindNodeByPath("0") != null) return $"Map/MapHelper.img/portal/game/{tn}/{im}/portalStart/0";
                }
            }
            // editor 回退（始终可见，仅静态渲染占位）
            var ed = helper.FindNodeByPath(true, "portal", "editor", typeName);
            if (ed?.FindNodeByPath("0") != null) return $"Map/MapHelper.img/portal/editor/{typeName}/0";
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
    /// 渲染整张地图（相机=地图中心）并按 mapId 缓存（ADR-0004）。桌面 crop 与设置预览共用此整图。
    /// 全程在 WzService 锁内（ExtractPng/GetOrigin 不自锁；锁串行化 → 缓存单写者，无 disposal 竞态）。
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
            var bmp = RenderCore(mapInfo, worldW, worldH);
            if (bmp == null) return null;
            _fullRender?.Dispose();
            _fullRender = bmp;
            _fullRenderMapId = mapInfo.Id;
            try { using var _i = SKImage.FromBitmap(_fullRender); System.IO.File.WriteAllBytes("/tmp/map_full.png", _i.Encode(SKEncodedImageFormat.Png, 100).ToArray()); } catch { }
            return _fullRender;
        });
    }

    /// <summary>整图渲染核心：worldW×worldH，相机中心=地图中心（cx=MinX+worldW/2），画布左上=(MinX,MinY)。</summary>
    private SKBitmap? RenderCore(MapInfo mapInfo, int w, int h)
    {
        try
        {
            int originWorldX = mapInfo.MinX;
            int originWorldY = mapInfo.MinY;
            int centerX = mapInfo.MinX + w / 2;
            int centerY = mapInfo.MinY + h / 2;

            var result = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(result))
            {
                canvas.Clear(SKColors.Transparent);

                // 1. 后景 back（Front=false），跳过 spine
                if (MapShowBack)
                {
                    foreach (var back in mapInfo.Backs.Where(b => !b.Front && b.Ani != 2))
                        DrawBack(canvas, back, centerX, centerY, originWorldX, originWorldY, w, h);
                }

                // 2. 8 层 obj + tile（对齐 reader drLayers/renderMapLayers：先 obj 按 z 排序，后 tile，tile 覆盖 obj）
                for (int i = 0; i < 8; i++)
                {
                    var layer = mapInfo.Layers[i];
                    if (MapShowObj)
                    {
                        foreach (var obj in layer.Objs.OrderBy(o => o.Z).ThenBy(o => o.Id))
                        {
                            if (obj.Resource.Frames.Count == 0) continue;
                            DrawSprite(canvas, obj.Resource.Frames[0].ResourceUrl, obj.X - originWorldX, obj.Y - originWorldY, obj.FlipX, 255);
                        }
                    }
                    if (MapShowTile)
                    {
                        foreach (var tile in layer.Tiles)
                            DrawSprite(canvas, tile.Resource.ResourceUrl, tile.X - originWorldX, tile.Y - originWorldY, false, 255);
                    }
                }

                // 3. life（NPC/Mob）— 对齐 Java handler drLife：层后绘制；定位 (X, Cy)（Cy 缺失回退 Y）。
                if (MapShowLife)
                {
                    foreach (var life in mapInfo.Lifes)
                    {
                        if (life.Hide || string.IsNullOrEmpty(life.Resource.ResourceUrl)) continue;
                        int ly = life.Cy != 0 ? life.Cy : life.Y;
                        if (life.Id < 3)
                        {
                            var _p = _wz.ExtractPng(life.Resource.ResourceUrl);
                            using var _b = _p != null ? SKBitmap.Decode(_p) : null;
                            Console.WriteLine($"[Life DRAW] id={life.Id} sx={life.X - originWorldX} sy={ly - originWorldY} bmp={_b?.Width}x{_b?.Height} flip={life.Flip}");
                        }
                        DrawSprite(canvas, life.Resource.ResourceUrl, life.X - originWorldX, ly - originWorldY, life.Flip, 255);
                    }
                }

                // 4. 前景 back（Front=true）
                if (MapShowBack)
                {
                    foreach (var back in mapInfo.Backs.Where(b => b.Front && b.Ani != 2))
                        DrawBack(canvas, back, centerX, centerY, originWorldX, originWorldY, w, h);
                }

                // 5. portal — 对齐 Java handler drPortals：front back 之后绘制；定位 (X, Y)，无 flip。
                if (MapShowPortal)
                {
                    foreach (var portal in mapInfo.Portals)
                    {
                        if (string.IsNullOrEmpty(portal.Resource.ResourceUrl)) continue;
                        DrawSprite(canvas, portal.Resource.ResourceUrl, portal.X - originWorldX, portal.Y - originWorldY, false, 255);
                    }
                }

                // 6. foothold 调试覆盖（默认关）：红色线段，对齐 Java handler drFootholds。
                if (MapShowFoothold)
                {
                    using var fhPaint = new SKPaint { Color = new SKColor(255, 0, 0, 180), IsAntialias = false, StrokeWidth = 2 };
                    foreach (var fh in mapInfo.Footholds)
                        canvas.DrawLine(fh.X1 - originWorldX, fh.Y1 - originWorldY, fh.X2 - originWorldX, fh.Y2 - originWorldY, fhPaint);
                }
            }
            return result;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] RenderCore: {ex.Message}"); return null; }
    }

    /// <summary>
    /// 从整图渲染取 crop 子区为 PNG（深拷贝，ADR-0004 / F3）。
    /// N1：cw<=0 归一化为整图（"无 crop=整图"唯一规则点）；F5：源矩形与 [0,worldW]×[0,worldH] 求交，越界留透明、尺寸不变。
    /// 内部 blit 到独立 SKBitmap（禁用 ExtractSubset 别名）→ 编码 PNG；返回字节不再依赖缓存。
    /// </summary>
    public byte[]? CropFull(MapInfo mapInfo, int cropX, int cropY, int cropW, int cropH)
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

                using var dst = new SKBitmap(cropW, cropH);
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
                using var img = SKImage.FromBitmap(dst);
                return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MapService] CropFull: {ex.Message}"); return null; }
        });
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

    private void DrawSprite(SKCanvas canvas, string wzPath, int screenX, int screenY, bool flipX, int alpha)
    {
        try
        {
            var png = _wz.ExtractPng(wzPath);
            if (png == null || png.Length == 0) return;
            using var bmp = SKBitmap.Decode(png);
            if (bmp == null) return;
            var (ox, oy) = _wz.GetOrigin(wzPath);
            float dx = screenX - ox, dy = screenY - oy;
            using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
            if (alpha < 255) paint.Color = new SKColor(255, 255, 255, (byte)alpha);
            if (flipX)
            {
                // 绕放置点 (screenX,screenY) 镜像（对齐 reader dr()：translate→scale(-1,1)→draw(-ox,-oy)）
                canvas.Save();
                canvas.Translate(screenX, screenY);
                canvas.Scale(-1, 1);
                canvas.DrawBitmap(bmp, -ox, -oy, paint);
                canvas.Restore();
            }
            else canvas.DrawBitmap(bmp, dx, dy, paint);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] DrawSprite({wzPath}): {ex.Message}"); }
    }

    /// <summary>
    /// 绘制背景（逐行对齐 maplestory-wiki-backend MapRenderHandler.drawBacks）：
    /// 视差 px = back.X + cx*(rx+100)/100（cx=地图中心；仅当非 ScrollHorizontal 即 (tm&amp;4)==0）；
    /// 画布坐标 bx = floor(px) - originWorldX；绘制减 origin（F6）；flip 绕放置点 bx（N2）。
    /// type 经 GetBackTileMode 重映射；tm bit0/1 水平/垂直平铺；screenMode!=0 跳过（F7）。
    /// </summary>
    private void DrawBack(SKCanvas canvas, MapBack back, int centerX, int centerY, int originWorldX, int originWorldY, int w, int h)
    {
        try
        {
            if (back.ScreenMode != 0) return;
            var png = _wz.ExtractPng(back.Resource.ResourceUrl);
            if (png == null || png.Length == 0) return;
            using var bmp = SKBitmap.Decode(png);
            if (bmp == null) return;
            var (ox, oy) = _wz.GetOrigin(back.Resource.ResourceUrl);

            int tm = GetBackTileMode(back.Type);
            float px = back.X;
            if ((tm & 4) == 0) px += centerX * (back.Rx + 100) / 100f;
            float py = back.Y;
            if ((tm & 8) == 0) py += centerY * (back.Ry + 100) / 100f;
            int bx = (int)Math.Floor(px) - originWorldX;
            int by = (int)Math.Floor(py) - originWorldY;

            int alpha = back.Alpha > 0 ? back.Alpha : 255;
            using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
            if (alpha < 255) paint.Color = new SKColor(255, 255, 255, (byte)alpha);

            int tw = back.Cx > 0 ? back.Cx : bmp.Width;
            int th = back.Cy > 0 ? back.Cy : bmp.Height;
            int tileW = (tm & 1) != 0 ? tw : 0;
            int tileH = (tm & 2) != 0 ? th : 0;

            if (tileW == 0 && tileH == 0)
            {
                DrawBackOnce(canvas, bmp, bx, by, ox, oy, back.FlipX, paint);
                return;
            }
            DrTiled(canvas, bmp, bx, by, tileW, tileH, w, h, ox, oy, back.FlipX, paint);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[MapService] DrawBack: {ex.Message}"); }
    }

    /// <summary>平铺覆盖画布：对齐后端 drTiled，l/r/t/b 用 floor/ceil ±1 padding。</summary>
    private void DrTiled(SKCanvas canvas, SKBitmap bmp, int bx, int by, int tw, int th, int w, int h, int ox, int oy, bool flipX, SKPaint paint)
    {
        int l = tw > 0 ? (int)Math.Floor((0d - bx) / tw) - 1 : 0;
        int r = tw > 0 ? (int)Math.Ceiling((w - bx) / (double)tw) + 1 : 1;
        int t = th > 0 ? (int)Math.Floor((0d - by) / th) - 1 : 0;
        int b = th > 0 ? (int)Math.Ceiling((h - by) / (double)th) + 1 : 1;
        for (int iy = t; iy < b; iy++)
            for (int ix = l; ix < r; ix++)
                DrawBackOnce(canvas, bmp, bx + ix * tw, by + iy * th, ox, oy, flipX, paint);
    }

    /// <summary>单次绘制 back：非 flip 画在 (x-ox, y-oy)；flip 绕放置点 (x,y) 镜像（N2，对齐 Java dr）。</summary>
    private static void DrawBackOnce(SKCanvas canvas, SKBitmap bmp, int x, int y, int ox, int oy, bool flipX, SKPaint paint)
    {
        if (flipX)
        {
            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(-1, 1);
            canvas.DrawBitmap(bmp, -ox, -oy, paint);
            canvas.Restore();
        }
        else canvas.DrawBitmap(bmp, x - ox, y - oy, paint);
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
