using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;
using SkiaSharp;
using WzComparerR2.WzLib;

namespace MiniPet.Services;

public class PaperdollService
{
    /// <summary>纸娃娃在 AnimService 中的哨兵 mobId（字符串常量；走未改动的 AnimService 管线）</summary>
    public const string SentinelMobId = "__paperdoll__";

    private readonly WzService _wz;
    private readonly CacheManager _cache;
    private readonly SpriteService _sprite;
    private CharacterAppearance? _current;
    private readonly Dictionary<string, CharacterAppearance> _presets = new();

    // 应用纸娃娃时预构建的动作（覆盖空闲 stand1 + 拖拽 walk1）
    private static readonly string[] BuildActions = { "stand1", "stand2", "walk1", "walk2", "jump" };

    // zmap 不可用时回退（顶→底）
    private static readonly string[] FallbackZmapTopToBottom =
    {
        "cap", "hairOverHead", "weaponOverHair", "cape", "shield", "weapon", "gloveOver", "glove",
        "coat", "pants", "shoe", "hand", "arm", "capeBack", "tail", "face", "ear",
        "head", "body", "hairBelowHead", "back", "effect"
    };

    private List<string>? _zmapCache;
    private readonly Dictionary<string, int> _zmapIndex = new();

    public PaperdollService(WzService wz, CacheManager cache, SpriteService sprite)
    {
        _wz = wz;
        _cache = cache;
        _sprite = sprite;
    }

    public bool LoadAppearance(CharacterAppearance appearance)
    {
        _current = appearance;
        // 重新加载外观时清掉 zmap 缓存（WZ 可能换过）
        _zmapCache = null;
        return true;
    }

    // ── body / head 身份（head = body + 10000）──
    private static int BodyNumber(CharacterAppearance a) => a.Gender == 0 ? 2000 : 2001;
    private static string BodyRoot(CharacterAppearance a) => $"Character/{BodyNumber(a):D8}.img";
    private static string HeadRoot(CharacterAppearance a) => $"Character/{BodyNumber(a) + 10000:D8}.img";

    private static bool HasId(ItemInfo? it) => it != null && !string.IsNullOrEmpty(it.Id) && int.TryParse(it.Id, out _);

    /// <summary>装备 id → WZ img 根路径（目录映射参考 MapleSalon2 itemFolder.ts，统一 8 位补零）。</summary>
    private static string ItemRoot(string id)
    {
        int n = int.TryParse(id, out var v) ? v : 0;
        int part = n / 10000;
        string folder = part switch
        {
            2 or 5 => "Face/",
            3 or 4 or 6 or 7 => "Hair/",
            100 => "Cap/",
            101 or 102 or 103 => "Accessory/",
            104 => "Coat/",
            105 => "Longcoat/",
            106 => "Pants/",
            107 => "Shoes/",
            108 => "Glove/",
            109 => "Shield/",
            110 => "Cape/",
            _ when part >= 112 && part <= 119 => "Accessory/",
            _ when part >= 121 && part <= 170 => "Weapon/",
            _ when part >= 190 && part <= 199 => "TamingMob/",
            _ => ""
        };
        return $"Character/{folder}{id.PadLeft(8, '0')}.img";
    }

    private IEnumerable<(string category, string root)> EnumerateRoots(CharacterAppearance a)
    {
        yield return ("body", BodyRoot(a));
        yield return ("head", HeadRoot(a));
        if (HasId(a.Hair)) yield return ("hair", ItemRoot(a.Hair.Id));
        if (HasId(a.Face)) yield return ("face", ItemRoot(a.Face.Id));
        if (HasId(a.Cap)) yield return ("cap", ItemRoot(a.Cap.Id));
        if (HasId(a.Cape)) yield return ("cape", ItemRoot(a.Cape.Id));
        if (HasId(a.Coat)) yield return ("coat", ItemRoot(a.Coat.Id));
        if (HasId(a.Pants)) yield return ("pants", ItemRoot(a.Pants.Id));
        if (HasId(a.Shoes)) yield return ("shoe", ItemRoot(a.Shoes.Id));
        if (HasId(a.Glove)) yield return ("glove", ItemRoot(a.Glove.Id));
        if (HasId(a.Shield)) yield return ("shield", ItemRoot(a.Shield.Id));
        if (HasId(a.Weapon)) yield return ("weapon", ItemRoot(a.Weapon.Id));
    }

    /// <summary>读取 zmap（顶→底）并构建层名→索引。WZ 不可用时用回退表。</summary>
    private List<string> GetZmapTopToBottom()
    {
        if (_zmapCache != null) return _zmapCache;
        var zmap = _wz.GetZmapOrder();
        if (zmap.Count == 0) zmap = FallbackZmapTopToBottom.ToList();
        _zmapIndex.Clear();
        for (int i = 0; i < zmap.Count; i++)
        {
            _zmapIndex[zmap[i]] = i;
            if (zmap[i].Length > 2) _zmapIndex[zmap[i].ToLowerInvariant()] = i;
        }
        _zmapCache = zmap;
        return zmap;
    }

    private string ResolveFaceAction(string root)
    {
        if (_wz.FindNodeByPath($"{root}/default") != null) return "default";
        if (_wz.FindNodeByPath($"{root}/blink") != null) return "blink";
        return "default";
    }

    private static readonly HashSet<string> EarPieces = new() { "ear", "humanEar", "lefEar", "highlefEar" };
    private static bool IsEarPiece(string n) => EarPieces.Contains(n);
    // 注：本 WZ 的 humanEar sprite 为空（透明），无法显示；故 Human 也用可见的精灵耳 "ear"。
    // Elf 同样用 "ear"；lefEar/highlefEar 仍各自独立（可见）。
    private string ExpectedEarPiece() => _current?.Ear switch
    {
        EarType.Lef => "lefEar",
        EarType.HighLef => "highlefEar",
        _ => "ear" // Human / Elf 都用精灵耳（humanEar 在本 WZ 为空图）
    };

    /// <summary>
    /// 收集某帧的全部 piece：遍历每个部件根的 {action}/{frame} 子节点。
    /// face 走表情分支（default/blink），其余走身体动作。
    /// </summary>
    private List<Piece> CollectPieces(string action, int frame)
    {
        var pieces = new List<Piece>();
        if (_current == null) return pieces;
        foreach (var (category, root) in EnumerateRoots(_current))
        {
            string act = category == "face" ? ResolveFaceAction(root) : action;
            int frm = category == "face" ? 0 : frame;
            // face 的动作节点(default/blink)下直接是 piece，无数字帧号；身体动作才是 {act}/{frame}
            var framePath = category == "face" ? $"{root}/{act}" : $"{root}/{act}/{frm}";
            if (_wz.FindNodeByPath(framePath) == null)
            {
                Console.WriteLine($"[Paperdoll DBG] {category}: framePath 不存在 {framePath}");
                if (category == "face")
                {
                    var acts = new List<string>();
                    var faceRootNode = _wz.FindNodeByPath(root);
                    if (faceRootNode != null)
                        foreach (Wz_Node c in faceRootNode.Nodes) acts.Add(c.Text);
                    Console.WriteLine($"[Paperdoll DBG] face {root} actions=[{string.Join(",", acts.Take(20))}]");
                    _wz.DiagPiece($"{root}/default");
                }
                continue;
            }

            var rawNames = _wz.GetFramePieceNames(framePath);
            if (category == "body" || category == "head" || category == "hair")
            {
                Console.WriteLine($"[Paperdoll DBG] {category} {framePath} rawChildren=[{string.Join(",", rawNames)}]");
                if (rawNames.Count > 0) _wz.DiagPiece($"{framePath}/{rawNames[0]}");
            }

            var foundNames = new List<string>();
            foreach (var pieceName in rawNames)
            {
                // 耳朵互斥：只渲染当前 EarType 对应的耳朵 piece（默认人类 humanEar）
                if (category == "head" && IsEarPiece(pieceName) && pieceName != ExpectedEarPiece()) continue;
                var piecePath = $"{framePath}/{pieceName}";
                var png = _wz.ExtractPng(piecePath);
                if (png == null || png.Length == 0) continue;
                var bmp = SKBitmap.Decode(png);
                if (bmp == null) continue;
                var (ox, oy) = _wz.GetOrigin(piecePath);
                var map = _wz.GetPieceMap(piecePath);
                var z = _wz.GetZField(piecePath) ?? "";
                foundNames.Add(pieceName);
                pieces.Add(new Piece
                {
                    Category = category,
                    PieceName = pieceName,
                    Bmp = bmp,
                    Ox = ox,
                    Oy = oy,
                    Map = map,
                    ZField = z
                });
            }
            Console.WriteLine($"[Paperdoll DBG] {category}: {framePath} → pieces=[{string.Join(",", foundNames)}]");
        }
        return pieces;
    }

    /// <summary>
    /// 锚点扩散：以 navel=(0,0) 为种子，件与件之间通过 map 锚点链（navel→neck→brow→hand…）相互对齐。
    /// 参考 MapleSalon2 itemPiece.buildAncher / characterBodyFrame。
    /// </summary>
    private void ResolveAnchors(List<Piece> pieces)
    {
        var anchors = new Dictionary<string, (int x, int y)> { ["navel"] = (0, 0) };
        int maxIter = pieces.Count * 4 + 8;
        for (int iter = 0; iter < maxIter; iter++)
        {
            bool changed = false;
            foreach (var p in pieces)
            {
                if (p.Locked || p.Map.Count == 0) continue;
                string? baseName = null;
                foreach (var k in p.Map.Keys)
                {
                    if (anchors.ContainsKey(k)) { baseName = k; break; }
                }
                if (baseName == null) continue;

                var A = anchors[baseName];
                var M = p.Map[baseName];
                p.AnchorX = A.x - M.x;
                p.AnchorY = A.y - M.y;
                p.BaseAnchor = baseName;
                foreach (var (other, off) in p.Map)
                {
                    if (other != baseName && !anchors.ContainsKey(other))
                        anchors[other] = (p.AnchorX + off.x, p.AnchorY + off.y);
                }
                p.Locked = true;
                changed = true;
            }
            if (!changed) break;
        }

        // 最终绘制位置 = anchor - origin；anchor = base锚点世界位(A) - piece图内map位(M)
        foreach (var p in pieces)
        {
            if (!p.Locked) continue;
            p.FinalX = p.AnchorX - p.Ox;
            p.FinalY = p.AnchorY - p.Oy;
        }
        foreach (var p in pieces)
            Console.WriteLine($"[Paperdoll DBG] piece {p.Category}/{p.PieceName} Ox={p.Ox} Oy={p.Oy} anchor=({p.AnchorX},{p.AnchorY}) map=[{string.Join(",", p.Map.Keys)}] base={p.BaseAnchor} final=({p.FinalX},{p.FinalY}) z={p.ZField}");
    }

    /// <summary>求每个 piece 归属的 zmap 层名与数值 z（顶=0，底=大），用于排序。</summary>
    private void AssignLayersAndZIndex(List<Piece> pieces)
    {
        foreach (var p in pieces)
        {
            p.ResolvedLayer = ResolveLayer(p);
            p.ZIndex = _zmapIndex.TryGetValue(p.ResolvedLayer, out var zi) ? zi : int.MaxValue;
        }
    }

    private string ResolveLayer(Piece p)
    {
        if (!string.IsNullOrEmpty(p.ZField))
        {
            if (_zmapIndex.ContainsKey(p.ZField)) return p.ZField;
            if (_zmapIndex.ContainsKey(p.ZField.ToLowerInvariant())) return p.ZField.ToLowerInvariant();
        }
        if (_zmapIndex.ContainsKey(p.PieceName)) return p.PieceName;
        if (_zmapIndex.ContainsKey(p.PieceName.ToLowerInvariant())) return p.PieceName.ToLowerInvariant();
        return p.Category switch
        {
            "body" => "body", "head" => "head", "face" => "face",
            "hair" => "hairOverHead", "cap" => "cap", "coat" => "coat",
            "pants" => "pants", "shoe" => "shoe", "glove" => "glove",
            "weapon" => "weapon", "shield" => "shield", "cape" => "cape",
            _ => p.PieceName
        };
    }

    /// <summary>单帧合成（自身包围盒），用于设置中心预览。</summary>
    public byte[]? CompositeFrame(string action, int frameIndex)
    {
        if (_current == null) return null;
        var zmap = GetZmapTopToBottom();
        var pieces = CollectPieces(action, frameIndex);
        try
        {
            if (pieces.Count == 0) return null;
            ResolveAnchors(pieces);
            AssignLayersAndZIndex(pieces);
            return CompositePieces(pieces);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] CompositeFrame: {ex.Message}"); return null; }
        finally { foreach (var p in pieces) p.Bmp?.Dispose(); }
    }

    private static byte[]? CompositePieces(List<Piece> pieces)
    {
        var locked = pieces.Where(p => p.Locked).ToList();
        if (locked.Count == 0) return null;
        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var p in locked)
        {
            minX = Math.Min(minX, p.FinalX); minY = Math.Min(minY, p.FinalY);
            maxX = Math.Max(maxX, p.FinalX + p.Bmp!.Width); maxY = Math.Max(maxY, p.FinalY + p.Bmp.Height);
        }
        int cw = maxX - minX, ch = maxY - minY;
        if (cw <= 0 || ch <= 0) return null;
        int ox = -minX, oy = -minY;

        using var result = new SKBitmap(cw, ch);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        // z 大（底）先画，z 小（顶）后画
        foreach (var p in locked.OrderByDescending(p => p.ZIndex))
            canvas.DrawBitmap(p.Bmp!, ox + p.FinalX, oy + p.FinalY);

        using var image = SKImage.FromBitmap(result);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>
    /// 构建某动作的精灵图条：先算所有帧的联合画布 + 公共锚点，再把每帧画进共享画布，水平拼接。
    /// 返回 (SpriteStrip, png字节)。
    /// </summary>
    public (SpriteStrip? strip, byte[]? png) BuildActionStrip(string action)
    {
        if (_current == null) return (null, null);
        var bodyRoot = BodyRoot(_current);
        int frameCount = GetActionFrameCount(bodyRoot, action);
        if (frameCount <= 0) return (null, null);

        var zmap = GetZmapTopToBottom();
        var frames = new List<List<Piece>>();
        try
        {
            int minX = 0, minY = 0, maxX = 0, maxY = 0;
            for (int f = 0; f < frameCount; f++)
            {
                var pieces = CollectPieces(action, f);
                ResolveAnchors(pieces);
                AssignLayersAndZIndex(pieces);
                frames.Add(pieces);
                foreach (var p in pieces.Where(x => x.Locked))
                {
                    minX = Math.Min(minX, p.FinalX); minY = Math.Min(minY, p.FinalY);
                    maxX = Math.Max(maxX, p.FinalX + p.Bmp!.Width); maxY = Math.Max(maxY, p.FinalY + p.Bmp.Height);
                }
            }
            int cw = maxX - minX, ch = maxY - minY;
            if (cw <= 0 || ch <= 0) return (null, null);

            int originX = -minX, originY = -minY;
            int stripW = cw * frameCount;

            using var strip = new SKBitmap(stripW, ch);
            using var canvas = new SKCanvas(strip);
            canvas.Clear(SKColors.Transparent);

            var frameData = new List<FrameData>();
            for (int f = 0; f < frameCount; f++)
            {
                int dx = f * cw + originX;
                foreach (var p in frames[f].Where(x => x.Locked).OrderByDescending(x => x.ZIndex))
                    canvas.DrawBitmap(p.Bmp!, dx + p.FinalX, originY + p.FinalY);

                int delay = _wz.GetDelay($"{bodyRoot}/{action}/{f}");
                frameData.Add(new FrameData { Index = f, Delay = delay > 0 ? delay : 100, A0 = 255, A1 = 255 });
            }

            using var image = SKImage.FromBitmap(strip);
            var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();

            var sp = new SpriteStrip
            {
                Action = action,
                FrameCount = frameCount,
                FrameWidth = cw,
                FrameHeight = ch,
                OriginX = originX,
                OriginY = originY,
                FrameData = frameData,
                DefaultDelay = 100
            };
            return (sp, png);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] BuildActionStrip({action}): {ex.Message}"); return (null, null); }
        finally
        {
            foreach (var fr in frames)
                foreach (var p in fr) p.Bmp?.Dispose();
        }
    }

    /// <summary>
    /// 为当前外观构建优先动作的精灵图条，缓存到哨兵 mobId（磁盘 + 内存），供未改动的 AnimService 播放。
    /// </summary>
    public bool BuildAndCacheAppearance(CharacterAppearance appearance)
    {
        try
        {
            _current = appearance;
            _zmapCache = null;
            bool any = false;
            foreach (var action in BuildActions)
            {
                var (strip, png) = BuildActionStrip(action);
                if (strip == null || png == null) continue;

                var config = new PetConfig
                {
                    Name = "paperdoll",
                    Type = "player",
                    Sprites = new Dictionary<string, SpriteStrip> { [action] = strip }
                };
                var configJson = System.Text.Json.JsonSerializer.Serialize(config);
                var configBytes = System.Text.Encoding.UTF8.GetBytes(configJson);

                _cache.SaveSpriteStrip(SentinelMobId, action, png, configBytes);
                _sprite.CacheStrip(SentinelMobId, action, strip, png);
                any = true;
            }
            return any;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] BuildAndCacheAppearance: {ex.Message}"); return false; }
    }

    private int GetActionFrameCount(string bodyRoot, string action)
    {
        try
        {
            return _wz.WithWzLock(() =>
            {
                var node = _wz.FindNodeByPath($"{bodyRoot}/{action}");
                if (node == null) return 0;
                int count = 0;
                foreach (Wz_Node child in node.Nodes)
                    if (int.TryParse(child.Text, out _)) count++;
                return count;
            });
        }
        catch { return 0; }
    }

    public List<string> GetActionList()
    {
        try
        {
            List<string>? actions = _wz.WithWzLock(() =>
            {
                var node = _wz.FindNodeByPath("Character/00002000.img");
                if (node == null) return null;
                var list = new List<string>();
                foreach (Wz_Node child in node.Nodes)
                    if (child.Text != "info") list.Add(child.Text);
                return list.Count > 0 ? list : null;
            });
            if (actions != null) return actions;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] GetActionList: {ex.Message}"); }
        return new() { "stand1", "stand2", "walk1", "walk2", "jump", "alert", "swingOF", "stabOF" };
    }

    public List<EquippedItem> GetEquipList(string category)
    {
        try
        {
            return _wz.WithWzLock(() =>
            {
                var result = new List<EquippedItem>();
                var dirNode = _wz.FindNodeByPath($"Character/{category}");
                if (dirNode == null) return result;
                foreach (Wz_Node child in dirNode.Nodes)
                {
                    var name = child.Text.Replace(".img", "");
                    if (int.TryParse(name, out _))
                    {
                        var nameNode = child.FindNodeByPath("info/name")?.GetValueEx<string>(null);
                        result.Add(new EquippedItem { Id = name, Name = nameNode ?? $"{category} {name}", Category = category });
                        if (result.Count >= 200) break;
                    }
                }
                return result;
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PaperdollService] GetEquipList: {ex.Message}"); return new(); }
    }

    public void SetEquip(string category, string itemId)
    {
        if (_current == null) return;
        var item = new ItemInfo { Id = itemId ?? string.Empty };
        switch (category)
        {
            case "Cap": _current.Cap = item; break;
            case "Cape": _current.Cape = item; break;
            case "Coat": _current.Coat = item; break;
            case "Pants": _current.Pants = item; break;
            case "Shoes": _current.Shoes = item; break;
            case "Weapon": _current.Weapon = item; break;
            case "Shield": _current.Shield = item; break;
            case "Glove": _current.Glove = item; break;
        }
    }

    public void RemoveEquip(string category)
    {
        if (_current == null) return;
        switch (category)
        {
            case "Cap": _current.Cap = null; break;
            case "Cape": _current.Cape = null; break;
            case "Coat": _current.Coat = null; break;
            case "Pants": _current.Pants = null; break;
            case "Shoes": _current.Shoes = null; break;
            case "Weapon": _current.Weapon = null; break;
            case "Shield": _current.Shield = null; break;
            case "Glove": _current.Glove = null; break;
        }
    }

    public void SavePreset(string name, CharacterAppearance appearance) { _presets[name] = appearance; }
    public CharacterAppearance? LoadPreset(string name) => _presets.TryGetValue(name, out var p) ? p : null;

    /// <summary>单件 piece 描述。</summary>
    private sealed class Piece
    {
        public string Category = "";
        public string PieceName = "";
        public SKBitmap? Bmp;
        public int Ox, Oy;
        public Dictionary<string, (int x, int y)> Map = new();
        public string ZField = "";
        public string ResolvedLayer = "";
        public int ZIndex;
        public int AnchorX, AnchorY;
        public string? BaseAnchor;
        public bool Locked;
        public int FinalX, FinalY;
    }
}

public class EquippedItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}
