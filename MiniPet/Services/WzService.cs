using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MiniPet.Models;
using MiniPet.Utils;
using WzComparerR2.WzLib;

namespace MiniPet.Services
{
    /// <summary>
    /// WZ 服务 — 基于 WzComparerR2.WzLib，参考 mapRender/backend/Program.cs 的调用模式
    /// </summary>
    public class WzService
    {
        private Wz_Structure? _wzs;
        private bool _wzLoaded;
        private string _baseWzPath = string.Empty;
        private WzError? _lastError;

        public bool IsLoaded => _wzLoaded;
        public WzError? LastError => _lastError;

        public (bool success, WzError? error) LoadWz(string wzLibPath, string baseWzPath)
        {
            _wzLoaded = false;
            _lastError = null;

            if (string.IsNullOrEmpty(baseWzPath) || !Directory.Exists(baseWzPath))
            {
                _lastError = WzError.WzPathInvalid;
                return (false, _lastError);
            }
            _baseWzPath = baseWzPath;

            try
            {
                // 自动探测 Base 子目录
                var wzFolder = baseWzPath;
                var baseSubDir = Path.Combine(baseWzPath, "Base");
                if (Directory.Exists(baseSubDir) && File.Exists(Path.Combine(baseSubDir, "Base.wz")))
                    wzFolder = baseSubDir;

                Console.WriteLine($"[WzService] 加载 WZ 目录: {wzFolder}");
                Wz_Structure.DefaultAutoDetectExtFiles = true;
                Wz_Structure.DefaultImgCheckDisabled = true;
                _wzs = new Wz_Structure();
                _wzs.LoadWzFolder(wzFolder, ref _wzs.WzNode, true);
                _wzLoaded = true;
                Console.WriteLine($"[WzService] Base 加载成功, imgs: {_wzs.img_number}");

                // 加载 Packs/*.ms — meta 数据（origin/delay/a0/a1）
                var packsDir = Path.Combine(baseWzPath, "Packs");
                if (!Directory.Exists(packsDir))
                    packsDir = Path.Combine(Path.GetDirectoryName(wzFolder)!, "Packs");
                if (Directory.Exists(packsDir))
                {
                    foreach (var msFile in Directory.GetFiles(packsDir, "*.ms"))
                    {
                        try
                        {
                            _wzs.LoadMsFile(msFile);
                            Console.WriteLine($"[WzService] Packs 加载: {Path.GetFileName(msFile)}");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WzService] Packs 加载失败 {msFile}: {ex.Message}");
                        }
                    }
                    foreach (var mnFile in Directory.GetFiles(packsDir, "*.mn"))
                    {
                        try
                        {
                            _wzs.LoadMsFile(mnFile);
                            Console.WriteLine($"[WzService] Packs 加载: {Path.GetFileName(mnFile)}");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WzService] Packs 加载失败 {mnFile}: {ex.Message}");
                        }
                    }
                }
                Console.WriteLine($"[WzService] 加载完成, imgs: {_wzs.img_number}");
                return (true, null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] WZ 加载失败: {ex.Message}");
                _lastError = WzError.Unknown;
                return (false, _lastError);
            }
        }

        public Wz_Node? FindNodeByPath(string path)
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            try
            {
                return _wzs.WzNode.FindNodeByPath(path.Replace('/', '\\'), true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] FindNodeByPath({path}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取 `img/action` 下的帧数（遍历 0,1,2... 直到 null）
        /// </summary>
        public int GetFrameCount(string wzPath)
        {
            var node = FindNodeByPath(wzPath);
            if (node == null) return 0;
            int count = 0;
            for (int f = 0; ; f++)
            {
                if (node.FindNodeByPath(f.ToString()) == null) break;
                count++;
            }
            return count;
        }

        /// <summary>
        /// 获取帧 PNG 数据
        /// </summary>
        public byte[]? ExtractPng(string wzPath)
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            try
            {
                var node = _wzs.WzNode.FindNodeByPath(true, wzPath.Split('/'));
                if (node == null) return null;
                // R2 GetLinkedSourceNode: _inlink/_outlink/source
                var linkNode = GetLinkedSourceNode(node) ?? node;
                var png = linkNode.GetValue<Wz_Png>() ?? node.GetValue<Wz_Png>();
                if (png == null) return null;
                return PngEncoder.Encode(PngEncoder.DecodePixels(png), png.Width, png.Height);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] ExtractPng({wzPath}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取 origin 坐标
        /// </summary>
        public (int x, int y) GetOrigin(string wzPath)
        {
            try
            {
                var node = FindNodeByPath(wzPath);
                if (node == null) return (0, 0);
                // origin 在原始帧节点上，不要 ResolveNode（会跳到 canvas 丢失 origin）
                var origin = node.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
                return origin != null ? (origin.X, origin.Y) : (0, 0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] GetOrigin({wzPath}): {ex.Message}");
                return (0, 0);
            }
        }

        /// <summary>
        /// 获取帧延迟
        /// </summary>
        public int GetDelay(string wzPath)
        {
            try
            {
                var node = FindNodeByPath(wzPath);
                if (node == null) return 100;
                return node.FindNodeByPath("delay")?.GetValueEx<int>(100) ?? 100;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetDelay: {ex.Message}"); return 100; }
        }

        /// <summary>
        /// 获取帧 a0 值
        /// </summary>
        public int GetA0(string wzPath)
        {
            try
            {
                var node = FindNodeByPath(wzPath);
                return node?.FindNodeByPath("a0")?.GetValueEx<int>(255) ?? 255;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetA0: {ex.Message}"); return 255; }
        }

        /// <summary>
        /// 获取帧 a1 值
        /// </summary>
        public int GetA1(string wzPath)
        {
            try
            {
                var node = FindNodeByPath(wzPath);
                return node?.FindNodeByPath("a1")?.GetValueEx<int>(255) ?? 255;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetA1: {ex.Message}"); return 255; }
        }

        public int GetIntProperty(string wzPath)
        {
            var node = FindNodeByPath(wzPath);
            return node?.GetValueEx<int>(0) ?? 0;
        }

        public string? GetStringProperty(string wzPath)
        {
            var node = FindNodeByPath(wzPath);
            return node?.GetValueEx<string>(null) ?? node?.Value?.ToString();
        }

        /// <summary>
        /// 从 String.wz 获取怪物名称  String/Mob.img/{mobId}/name
        /// </summary>
        public string GetMobName(int mobId)
        {
            try
            {
                var node = FindNodeByPath($"String/Mob.img/{mobId}");
                if (node == null) return string.Empty;
                var nameNode = node.FindNodeByPath("name");
                return nameNode?.GetValueEx<string>(null) ?? string.Empty;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetMobName: {ex.Message}"); return string.Empty; }
        }

        /// <summary>
        /// 从 String.wz 获取 NPC 名称  String/Npc.img/{npcId}/name
        /// </summary>
        public string GetNpcName(int npcId)
        {
            try
            {
                var node = FindNodeByPath($"String/Npc.img/{npcId}");
                if (node == null) return string.Empty;
                var nameNode = node.FindNodeByPath("name");
                return nameNode?.GetValueEx<string>(null) ?? string.Empty;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetNpcName: {ex.Message}"); return string.Empty; }
        }

        /// <summary>
        /// 导出精灵图条和配置，支持 Mob/NPC 类型
        /// </summary>
        public (SpriteStrip? strip, PetConfig? config) ExportSpriteStrip(int id, string action, string type = "Mob")
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return (null, null);
            try
            {
                // Meta: {type}/{id}.img — 含 origin/delay/a0/a1
                var metaImgNode = _wzs.WzNode.FindNodeByPath(true, $"{type}/{id:D7}.img".Split('/'));
                if (metaImgNode == null) return (null, null);
                var metaImg = metaImgNode.GetValue<Wz_Image>();
                if (metaImg == null || !metaImg.TryExtract()) return (null, null);
                var actionNode = metaImg.Node.Nodes.FirstOrDefault(n => n.Text == action);
                if (actionNode == null) return (null, null);

                int maxL = 0, maxT = 0, maxR = 0, maxB = 0;
                var frameDataList = new List<FrameData>();
                int f = 0;
                foreach (Wz_Node child in actionNode.Nodes)
                {
                    var origin = child.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
                    int ox = origin?.X ?? 0, oy = origin?.Y ?? 0;
                    int delay = child.FindNodeByPath("delay")?.GetValueEx<int>(100) ?? 100;
                    int a0 = child.FindNodeByPath("a0")?.GetValueEx<int>(255) ?? 255;
                    int a1 = child.FindNodeByPath("a1")?.GetValueEx<int>(255) ?? 255;

                    var pngNode = child.ResolveUol() ?? child;
                    var linkNode = GetLinkedSourceNode(pngNode) ?? pngNode;
                    var png = linkNode.GetValue<Wz_Png>() ?? pngNode.GetValue<Wz_Png>();
                    int fw = png?.Width ?? 0, fh = png?.Height ?? 0;

                    maxL = Math.Max(maxL, ox);
                    maxT = Math.Max(maxT, oy);
                    maxR = Math.Max(maxR, fw - ox);
                    maxB = Math.Max(maxB, fh - oy);

                    frameDataList.Add(new FrameData { Index = f++, Delay = delay, A0 = a0, A1 = a1 });
                }
                if (frameDataList.Count == 0) return (null, null);

                var strip = new SpriteStrip
                {
                    Action = action,
                    FrameCount = frameDataList.Count,
                    FrameWidth = maxL + maxR,
                    FrameHeight = maxT + maxB,
                    OriginX = maxL,
                    OriginY = maxT,
                    FrameData = frameDataList,
                    DefaultDelay = 100
                };

                var config = new PetConfig
                {
                    Name = $"{type.ToLower()}_{id}",
                    Type = type.ToLower(),
                    OriginX = 0,
                    OriginY = 0,
                    Sprites = new Dictionary<string, SpriteStrip> { [action] = strip }
                };

                return (strip, config);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] ExportSpriteStrip({id}/{action}/{type}): {ex.Message}");
                return (null, null);
            }
        }

        // 保留旧签名兼容
        public (SpriteStrip? strip, PetConfig? config) ExportSpriteStrip(int mobId, string action) => ExportSpriteStrip(mobId, action, "Mob");

        /// <summary>
        /// 从 WZ 提取精灵图条 PNG（拼接所有帧为水平条带），支持 Mob/NPC
        /// </summary>
        public byte[]? BuildSpriteStripPng(int id, string action, string type = "Mob")
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            try
            {
                // Meta: {type}/{id}.img — origin 在 child 上，PNG 通过 child outlink 取
                var metaImgNode = _wzs.WzNode.FindNodeByPath(true, $"{type}/{id:D7}.img".Split('/'));
                if (metaImgNode == null) return null;
                var metaImg = metaImgNode.GetValue<Wz_Image>();
                if (metaImg == null || !metaImg.TryExtract()) return null;
                var actionNode = metaImg.Node.Nodes.FirstOrDefault(n => n.Text == action);
                if (actionNode == null) return null;

                var frames = new List<(byte[] bgra, int w, int h, int ox, int oy)>();
                foreach (Wz_Node child in actionNode.Nodes)
                {
                    var origin = child.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);

                    var pngNode = child.ResolveUol() ?? child;
                    var linkNode = GetLinkedSourceNode(pngNode) ?? pngNode;
                    var png = linkNode.GetValue<Wz_Png>() ?? pngNode.GetValue<Wz_Png>();
                    if (png == null) continue;

                    frames.Add((PngEncoder.DecodePixels(png), png.Width, png.Height, origin?.X ?? 0, origin?.Y ?? 0));
                }
                if (frames.Count == 0) return null;

                int mL = frames.Max(f => f.ox), mT = frames.Max(f => f.oy);
                int mR = frames.Max(f => f.w - f.ox), mB = frames.Max(f => f.h - f.oy);
                int cw = mL + mR, ch = mT + mB;
                int finalOx = mL, finalOy = mT;

                var stripBgra = new byte[cw * frames.Count * ch * 4];
                for (int i = 0; i < frames.Count; i++)
                {
                    var (bgra, fw, fh, ox, oy) = frames[i];
                    int dx = finalOx - ox, dy = finalOy - oy;
                    for (int y = 0; y < fh; y++)
                    {
                        int dstY = y + dy;
                        if (dstY < 0 || dstY >= ch) continue;
                        int src = y * fw * 4;
                        int dst = (dstY * cw * frames.Count + i * cw + dx) * 4;
                        int len = Math.Min(fw * 4, (cw - dx) * 4);
                        if (len > 0 && src + len <= bgra.Length && dst + len <= stripBgra.Length)
                            Buffer.BlockCopy(bgra, src, stripBgra, dst, len);
                    }
                }
                return PngEncoder.Encode(stripBgra, cw * frames.Count, ch);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] BuildSpriteStripPng({id}/{action}/{type}): {ex.Message}");
                return null;
            }
        }

        // 保留旧签名兼容
        public byte[]? BuildSpriteStripPng(int mobId, string action) => BuildSpriteStripPng(mobId, action, "Mob");

        /// <summary>
        /// 获取动作列表 — 支持 Mob/NPC 类型，从含动作元数据的 {type}/{id}.img 枚举动作节点
        /// </summary>
        public List<string> GetActionList(int id, string type = "Mob")
        {
            var result = new List<string>();
            try
            {
                if (!_wzLoaded || _wzs?.WzNode == null) return result;

                var imgNode = _wzs.WzNode.FindNodeByPath(true, $"{type}/{id:D7}.img".Split('/'));
                if (imgNode == null) return result;
                var img = imgNode.GetValue<Wz_Image>();
                if (img == null || !img.TryExtract()) return result;

                foreach (Wz_Node child in img.Node.Nodes)
                {
                    if (int.TryParse(child.Text, out _)) continue;
                    if (child.Nodes.Any(frame => int.TryParse(frame.Text, out _)))
                        result.Add(child.Text);
                }

                var priority = new[] { "stand", "move", "fly", "walk", "jump", "attack1", "skill1", "hit", "die" };
                result = result.Distinct(StringComparer.Ordinal).OrderBy(a =>
                {
                    var idx = Array.IndexOf(priority, a);
                    return idx >= 0 ? idx : priority.Length;
                }).ThenBy(a => a, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] GetActionList({id}/{type}): {ex.Message}");
            }
            return result;
        }

        // 保留旧签名兼容
        public List<string> GetActionList(int mobId) => GetActionList(mobId, "Mob");

        public Dictionary<string, List<string>> AutoDiscoverMappings(int mobId)
        {
            var result = new Dictionary<string, List<string>>();
            var actions = GetActionList(mobId);
            foreach (var action in actions)
            {
                result[action] = new List<string>();

                var node = FindNodeByPath($"Mob/_Canvas/{mobId:D7}.img/{action}");
                if (node == null) continue;
                for (int f = 0; ; f++)
                {
                    if (node.FindNodeByPath(f.ToString()) == null) break;
                    result[action].Add($"Mob/{mobId}.img/{action}/{f}");
                }
            }
            return result;
        }

        public string GetCharacterPartWzPath(string partType, int itemId, string action, int frame)
        {
            var category = partType switch
            {
                "Cap" => "Cap", "Cape" => "Cape", "Coat" => "Coat", "Longcoat" => "Longcoat",
                "Pants" => "Pants", "Shoes" => "Shoes", "Weapon" => "Weapon",
                "Shield" => "Shield", "Glove" => "Glove", "Earring" => "Earring",
                "Face" => "Face", "Hair" => "Hair", "Skin" => "Skin",
                "Body" => "Body", "Head" => "Head",
                _ => partType
            };
            return $"Character/{category}/{itemId:D8}.img/{action}/{frame}";
        }

        public string GetMapWzPath(int mapId)
        {
            var prefix = mapId / 100000000;
            return $"Map/Map/Map{prefix}/{mapId}.img";
        }

        public string GetMobWzPath(int mobId, string action, int frame) =>
            $"Mob/_Canvas/{mobId:D7}.img/{action}/{frame}";

        public string GetNpcWzPath(int npcId, string action, int frame) =>
            $"Npc/_Canvas/{npcId:D7}.img/{action}/{frame}";

        public string GetBalloonTilePath(int balloonId, string tileName) =>
            $"UI/ChatBalloon.img/{balloonId}/{tileName}";

        public List<(int Id, string Name)> GetDirectoryChildren(string wzDirectory)
        {
            var result = new List<(int Id, string Name)>();
            try
            {
                var node = FindNodeByPath($"{wzDirectory}/_Canvas");
                if (node == null) return result;
                foreach (Wz_Node child in node.Nodes)
                {
                    var key = child.Text;
                    if (!key.EndsWith(".img")) continue;
                    var cleanKey = key[..^4];
                    if (int.TryParse(cleanKey, out var id))
                        result.Add((id, key));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] GetDirectoryChildren({wzDirectory}): {ex.Message}");
            }
            return result;
        }

        public List<int> ListMobIds() =>
            GetDirectoryChildren("Mob").Select(x => x.Id).Where(id => id > 0).ToList();

        public List<int> ListNpcIds() =>
            GetDirectoryChildren("Npc").Select(x => x.Id).Where(id => id > 0).ToList();

        /// <summary>
        /// 节点解析：UOL → source/_inlink/_outlink → PNG
        /// 参考 mapRender/backend/Program.cs GetLinkedSourceNode
        /// </summary>
        private static Wz_Node ResolveNode(Wz_Node node)
        {
            try
            {
                // 1. 解析 UOL
                node = node.ResolveUol() ?? node;

                // 2. 解析链接
                var linkedNode = GetLinkedSourceNode(node);
                return linkedNode ?? node;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] ResolveNode({node.FullPathToFile}): {ex.Message}");
                return node;
            }
        }

        private static Wz_Node? GetLinkedSourceNode(Wz_Node node)
        {
            try
            {
                var sourceAc = node.Nodes["source"].GetValueEx<string>(null);
                if (!string.IsNullOrEmpty(sourceAc))
                {
                    var wzFile = node.GetNodeWzFile();
                    return wzFile?.WzStructure?.WzNode.FindNodeByPath(true, sourceAc.Split('/'));
                }

                var inlink = node.Nodes["_inlink"].GetValueEx<string>(null);
                if (!string.IsNullOrEmpty(inlink))
                {
                    var img = node.GetNodeWzImage();
                    return img?.Node.FindNodeByPath(true, inlink.Split('/'));
                }

                var outlink = node.Nodes["_outlink"].GetValueEx<string>(null);
                if (!string.IsNullOrEmpty(outlink))
                {
                    var wzFile = node.GetNodeWzFile();
                    return wzFile?.WzStructure?.WzNode.FindNodeByPath(true, outlink.Split('/'));
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetLinkedSourceNode: {ex.Message}"); }
            return null;
        }
    }
}
