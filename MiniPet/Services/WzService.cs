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
                _wzs = new Wz_Structure();
                _wzs.LoadWzFolder(wzFolder, ref _wzs.WzNode, true);
                _wzLoaded = true;
                Console.WriteLine($"[WzService] WZ 加载成功, 目录: {wzFolder}, 版本: {_wzs.wz_files[0].Header.WzVersion}");
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
                return _wzs.WzNode.FindNodeByPath(true, path.Split('/'));
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
                node = ResolveNode(node);
                return ExtractPngFromNode(node);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] ExtractPng({wzPath}): {ex.Message}");
                return null;
            }
        }

        private static byte[]? ExtractPngFromNode(Wz_Node node)
        {
            var png = node.GetValue<Wz_Png>();
            if (png == null) return null;
            var rawData = png.GetRawData();
            if (rawData == null || rawData.Length == 0) return null;
            return PngEncoder.Encode(rawData, png.Width, png.Height, PngPixelFormat.Bgra);
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
                node = ResolveNode(node);
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
        /// 导出精灵图条和配置
        /// </summary>
        public (SpriteStrip? strip, PetConfig? config) ExportSpriteStrip(int mobId, string action)
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return (null, null);
            try
            {
                var imgNode = _wzs.WzNode.FindNodeByPath(true, $"Mob/_Canvas/{mobId:D7}.img".Split('/'));
                if (imgNode == null) return (null, null);
                var img = imgNode.GetValue<Wz_Image>();
                if (img == null || !img.TryExtract()) return (null, null);

                var actionNode = img.Node.Nodes.FirstOrDefault(n => n.Text == action);
                if (actionNode == null) return (null, null);

                var frameDataList = new List<FrameData>();
                int originX = 0, originY = 0;
                int frameWidth = 0, frameHeight = 0;

                int f = 0;
                foreach (Wz_Node child in actionNode.Nodes)
                {
                    var resolved = child.ResolveUol() ?? child;
                    var delay = resolved.FindNodeByPath("delay")?.GetValueEx<int>(100) ?? 100;
                    var a0 = resolved.FindNodeByPath("a0")?.GetValueEx<int>(255) ?? 255;
                    var a1 = resolved.FindNodeByPath("a1")?.GetValueEx<int>(255) ?? 255;
                    var png = resolved.GetValue<Wz_Png>();
                    if (png != null)
                    {
                        frameWidth = Math.Max(frameWidth, png.Width);
                        frameHeight = Math.Max(frameHeight, png.Height);
                    }
                    var origin = resolved.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
                    if (origin != null)
                    {
                        originX = Math.Max(originX, origin.X);
                        originY = Math.Max(originY, origin.Y);
                    }

                    frameDataList.Add(new FrameData
                    {
                        Index = f++,
                        Delay = delay,
                        A0 = a0,
                        A1 = a1
                    });
                }

                if (frameDataList.Count == 0) return (null, null);

                var strip = new SpriteStrip
                {
                    Action = action,
                    FrameCount = frameDataList.Count,
                    FrameWidth = frameWidth,
                    FrameHeight = frameHeight,
                    OriginX = originX,
                    OriginY = originY,
                    FrameData = frameDataList,
                    DefaultDelay = 120
                };

                var config = new PetConfig
                {
                    Name = $"mob_{mobId}",
                    Type = "mob",
                    OriginX = originX,
                    OriginY = originY,
                    Sprites = new Dictionary<string, SpriteStrip> { [action] = strip }
                };

                return (strip, config);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] ExportSpriteStrip({mobId}/{action}): {ex.Message}");
                return (null, null);
            }
        }

        /// <summary>
        /// 从 WZ 提取精灵图条 PNG（拼接所有帧为水平条带）
        /// </summary>
        public byte[]? BuildSpriteStripPng(int mobId, string action)
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            try
            {
                var imgNode = _wzs.WzNode.FindNodeByPath(true, $"Mob/_Canvas/{mobId:D7}.img".Split('/'));
                if (imgNode == null) return null;
                var img = imgNode.GetValue<Wz_Image>();
                if (img == null || !img.TryExtract()) return null;

                var actionNode = img.Node.Nodes.FirstOrDefault(n => n.Text == action);
                if (actionNode == null) return null;

                var frames = new List<(byte[] rawBgra, int w, int h)>();
                foreach (Wz_Node child in actionNode.Nodes)
                {
                    var resolved = child.ResolveUol() ?? child;
                    var png = resolved.GetValue<Wz_Png>();
                    if (png == null) continue;
                    var raw = png.GetRawData();
                    if (raw == null || raw.Length == 0) continue;
                    frames.Add((raw, png.Width, png.Height));
                }
                if (frames.Count == 0) return null;

                var maxW = frames.Max(f => f.w);
                var maxH = frames.Max(f => f.h);
                var totalW = maxW * frames.Count;
                // Build strip from raw BGRA pixels (B,G,R,A per pixel)
                var stripRaw = new byte[totalW * maxH * 4];
                for (int i = 0; i < stripRaw.Length; i += 4) { stripRaw[i + 3] = 0; } // transparent
                for (int i = 0; i < frames.Count; i++)
                {
                    var (bgra, fw, fh) = frames[i];
                    for (int y = 0; y < fh && y < maxH; y++)
                    {
                        for (int x = 0; x < fw && x < maxW; x++)
                        {
                            int src = (y * fw + x) * 4;
                            int dst = (y * totalW + i * maxW + x) * 4;
                            if (src + 3 < bgra.Length && dst + 3 < stripRaw.Length)
                            {
                                stripRaw[dst] = bgra[src];       // B
                                stripRaw[dst + 1] = bgra[src + 1]; // G
                                stripRaw[dst + 2] = bgra[src + 2]; // R
                                stripRaw[dst + 3] = bgra[src + 3]; // A
                            }
                        }
                    }
                }
                return PngEncoder.Encode(stripRaw, totalW, maxH, PngPixelFormat.Bgra);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] BuildSpriteStripPng({mobId}/{action}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取怪物的动作列表 — 遍历 img 子节点，排除纯数字（帧号）子节点
        /// </summary>
        public List<string> GetActionList(int mobId)
        {
            var result = new List<string>();
            try
            {
                var imgNode = _wzs?.WzNode?.FindNodeByPath(true, $"Mob/_Canvas/{mobId:D7}.img".Split('/'));
                if (imgNode == null) return result;
                var img = imgNode.GetValue<Wz_Image>();
                if (img == null || !img.TryExtract()) return result;
                foreach (Wz_Node child in img.Node.Nodes)
                {
                    if (!int.TryParse(child.Text, out _))
                        result.Add(child.Text);
                }
                // 按顺序返回常见动作
                var priority = new[] { "stand", "move", "fly", "walk", "jump", "attack1", "skill1", "hit", "die" };
                result = result.OrderBy(a =>
                {
                    var idx = Array.IndexOf(priority, a);
                    return idx >= 0 ? idx : 99;
                }).ThenBy(a => a).ToList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] GetActionList({mobId}): {ex.Message}");
            }
            return result;
        }

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
                // WZ 数据在 _Canvas 子目录下
                var node = FindNodeByPath($"{wzDirectory}/_Canvas");
                if (node == null) return result;
                foreach (Wz_Node child in node.Nodes)
                {
                    var key = child.Text;
                    var cleanKey = key.EndsWith(".img") ? key[..^4] : key;
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
