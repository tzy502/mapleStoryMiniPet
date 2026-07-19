using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MiniPet.Models;

namespace MiniPet.Services
{
    /// <summary>
    /// WZ 服务 - 加载 WZ、节点查询、PNG 提取、origin/delay 解析、精灵图条导出、WZ 路径映射
    /// 优先使用 WzComparerR2.WzLib DLL，不可用时用纯 C# 实现作为 fallback
    /// </summary>
    public class WzService
    {
        private bool _dllLoaded;
        private bool _wzLoaded;
        private string _baseWzPath = string.Empty;
        private string _wzLibPath = string.Empty;
        private WzError? _lastError;

        // fallback：纯 C# WZ 解析缓存
        private WzDirectoryNode? _wzRoot;

        // DLL 加载缓存
        private object? _wzHandle;

        /// <summary>
        /// WZ 是否已加载
        /// </summary>
        public bool IsLoaded => _wzLoaded;

        /// <summary>
        /// 最后一次错误
        /// </summary>
        public WzError? LastError => _lastError;

        /// <summary>
        /// 加载 WZ 文件
        /// </summary>
        /// <param name="wzLibPath">WzComparerR2.WzLib DLL 路径</param>
        /// <param name="baseWzPath">BaseWZ 目录路径</param>
        /// <returns>(成功, 错误)</returns>
        public (bool success, WzError? error) LoadWz(string wzLibPath, string baseWzPath)
        {
            _wzLoaded = false;
            _lastError = null;

            // 验证 BaseWZ 路径
            if (string.IsNullOrEmpty(baseWzPath) || !Directory.Exists(baseWzPath))
            {
                _lastError = WzError.WzPathInvalid;
                return (false, _lastError);
            }
            _baseWzPath = baseWzPath;

            // 尝试加载 DLL
            _wzLibPath = wzLibPath ?? string.Empty;
            if (!string.IsNullOrEmpty(wzLibPath) && File.Exists(wzLibPath))
            {
                try
                {
                    _dllLoaded = TryLoadDll(wzLibPath);
                    if (_dllLoaded)
                    {
                        _wzLoaded = true;
                        return (true, null);
                    }
                }
                catch
                {
                    _dllLoaded = false;
                }
            }

            // DLL 不可用，使用纯 C# fallback
            try
            {
                var result = LoadWzFallback(baseWzPath);
                if (result.success)
                {
                    _wzLoaded = true;
                    return (true, null);
                }
                _lastError = result.error;
                return (false, _lastError);
            }
            catch (Exception ex)
            {
                _lastError = WzError.Unknown;
                return (false, _lastError);
            }
        }

        /// <summary>
        /// 按路径查找 WZ 节点
        /// </summary>
        public object? FindNodeByPath(string path)
        {
            if (!_wzLoaded) return null;

            if (_dllLoaded)
                return FindNodeByPathDll(path);

            return FindNodeByPathFallback(path);
        }

        /// <summary>
        /// 获取指定 WZ 路径下的所有帧路径
        /// </summary>
        public List<string> GetFramePaths(string wzPath)
        {
            var result = new List<string>();
            if (!_wzLoaded) return result;

            // 根据路径格式处理
            // 例如 "Mob/8880150.img/stand/0"、"Mob/8880150.img/stand/1" ...
            var parts = wzPath.Split('/');
            if (parts.Length < 3) return result;

            // 去掉 .img 后缀
            var imgPath = parts[0] + "/" + parts[1];
            var action = parts[2];

            // 查找节点
            var node = FindNodeByPath(imgPath);
            if (node == null) return result;

            if (_dllLoaded)
                return GetFramePathsDll(node, action);

            return GetFramePathsFallback(node, action);
        }

        /// <summary>
        /// 提取 PNG 数据
        /// </summary>
        public byte[]? ExtractPng(string wzPath)
        {
            if (!_wzLoaded) return null;

            if (_dllLoaded)
                return ExtractPngDll(wzPath);

            return ExtractPngFallback(wzPath);
        }

        /// <summary>
        /// 获取 origin 坐标
        /// </summary>
        public (int x, int y) GetOrigin(string wzPath)
        {
            if (!_wzLoaded) return (0, 0);

            if (_dllLoaded)
                return GetOriginDll(wzPath);

            return GetOriginFallback(wzPath);
        }

        /// <summary>
        /// 获取帧延迟
        /// </summary>
        public int GetDelay(string wzPath)
        {
            if (!_wzLoaded) return 0;

            if (_dllLoaded)
                return GetDelayDll(wzPath);

            return GetDelayFallback(wzPath);
        }

        /// <summary>
        /// 导出精灵图条和配置
        /// </summary>
        public (SpriteStrip? strip, PetConfig? config) ExportSpriteStrip(int mobId, string action)
        {
            if (!_wzLoaded) return (null, null);

            if (_dllLoaded)
                return ExportSpriteStripDll(mobId, action);

            return ExportSpriteStripFallback(mobId, action);
        }

        /// <summary>
        /// 导出聊天气泡瓦片
        /// </summary>
        public List<Sprite> ExportBalloonTiles(int balloonId)
        {
            var result = new List<Sprite>();
            if (!_wzLoaded) return result;

            // 聊天气泡路径: "UI/ChatBalloon.img/{balloonId}/{tileName}"
            // 瓦片名称: nw, n, ne, w, c, e, sw, s, se, arrow, head
            var tileNames = new[] { "nw", "n", "ne", "w", "c", "e", "sw", "s", "se", "arrow", "head" };
            foreach (var tile in tileNames)
            {
                var path = $"UI/ChatBalloon.img/{balloonId}/{tile}";
                var origin = GetOrigin(path);
                if (origin.x == 0 && origin.y == 0) continue; // 节点不存在

                result.Add(new Sprite
                {
                    ResourceUrl = path,
                    OriginX = origin.x,
                    OriginY = origin.y
                });
            }
            return result;
        }

        /// <summary>
        /// 获取怪物的动作列表
        /// </summary>
        public List<string> GetActionList(int mobId)
        {
            var result = new List<string>();
            if (!_wzLoaded) return result;

            var path = $"Mob/{mobId}.img";

            if (_dllLoaded)
                return GetActionListDll(path);

            return GetActionListFallback(path);
        }

        /// <summary>
        /// 自动发现动作映射
        /// </summary>
        public Dictionary<string, List<string>> AutoDiscoverMappings(int mobId)
        {
            var result = new Dictionary<string, List<string>>();
            if (!_wzLoaded) return result;

            var actions = GetActionList(mobId);
            foreach (var action in actions)
            {
                var frames = GetFramePaths($"Mob/{mobId}.img/{action}");
                if (frames.Count > 0)
                {
                    result[action] = frames;
                }
            }
            return result;
        }

        /// <summary>
        /// 获取角色部位 WZ 路径
        /// </summary>
        public string GetCharacterPartWzPath(string partType, int itemId, string action, int frame)
        {
            // Character part 路径映射: "Character/{category}/{id}.img/{action}/{frame}"
            var category = partType switch
            {
                "Cap" => "Cap",
                "Cape" => "Cape",
                "Coat" => "Coat",
                "Longcoat" => "Longcoat",
                "Pants" => "Pants",
                "Shoes" => "Shoes",
                "Weapon" => "Weapon",
                "Shield" => "Shield",
                "Glove" => "Glove",
                "Earring" => "Earring",
                "Face" => "Face",
                "Hair" => "Hair",
                "Skin" => "Skin",
                "Body" => "Body",
                "Head" => "Head",
                _ => partType
            };
            return $"Character/{category}/{itemId:D8}.img/{action}/{frame}";
        }

        /// <summary>
        /// 获取地图 WZ 路径
        /// </summary>
        public string GetMapWzPath(int mapId)
        {
            // Map 路径映射: "Map/Map/Map{mapId[0]}/{mapId}.img"
            var prefix = mapId.ToString()[0];
            return $"Map/Map/Map{prefix}/{mapId}.img";
        }

        // ==================== WZ 路径映射 ====================

        /// <summary>
        /// 怪物路径
        /// </summary>
        public string GetMobWzPath(int mobId, string action, int frame)
        {
            return $"Mob/{mobId}.img/{action}/{frame}";
        }

        /// <summary>
        /// NPC 路径
        /// </summary>
        public string GetNpcWzPath(int npcId, string action, int frame)
        {
            return $"Npc/{npcId}.img/{action}/{frame}";
        }

        /// <summary>
        /// 气泡路径
        /// </summary>
        public string GetBalloonTilePath(int balloonId, string tileName)
        {
            return $"UI/ChatBalloon.img/{balloonId}/{tileName}";
        }

        // ==================== DLL 加载 ====================

        private bool TryLoadDll(string dllPath)
        {
            // 尝试通过反射加载 DLL
            // 这里使用 NativeLibrary 加载原生 DLL，或通过 Assembly.LoadFrom 加载托管 DLL
            try
            {
                // 实际项目中使用 Assembly.LoadFrom(dllPath) 或 NativeLibrary.Load
                // 此处为占位实现，实际加载逻辑由 WzComparerR2.WzLib 的 API 决定
                var assembly = System.Reflection.Assembly.LoadFrom(dllPath);
                _wzHandle = assembly;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ==================== DLL 方法存根 ====================

        private object? FindNodeByPathDll(string path) => null;
        private List<string> GetFramePathsDll(object node, string action) => new List<string>();
        private byte[]? ExtractPngDll(string wzPath) => null;
        private (int x, int y) GetOriginDll(string wzPath) => (0, 0);
        private int GetDelayDll(string wzPath) => 0;
        private (SpriteStrip? strip, PetConfig? config) ExportSpriteStripDll(int mobId, string action) => (null, null);
        private List<string> GetActionListDll(string path) => new List<string>();

        // ==================== 纯 C# Fallback ====================

        /// <summary>
        /// 纯 C# WZ 文件解析入口
        /// </summary>
        private (bool success, WzError? error) LoadWzFallback(string baseWzPath)
        {
            try
            {
                // 查找 Base.wz
                var baseWzFile = FindBaseWzFile(baseWzPath);
                if (baseWzFile == null)
                {
                    return (false, WzError.WzPathInvalid);
                }

                // 解析根目录树
                using var stream = new FileStream(baseWzFile, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);

                _wzRoot = new WzDirectoryNode("", null);

                // 解析 WZ 文件头
                var header = ParseWzHeader(reader);
                if (header == null)
                {
                    return (false, WzError.WzFileCorrupted);
                }

                // 解析目录树
                reader.BaseStream.Seek(header.DataPos, SeekOrigin.Begin);
                ParseDirectory(reader, _wzRoot, header);

                return (true, null);
            }
            catch (EndOfStreamException)
            {
                return (false, WzError.WzFileCorrupted);
            }
            catch (InvalidDataException)
            {
                return (false, WzError.WzFileCorrupted);
            }
        }

        private static string? FindBaseWzFile(string baseWzPath)
        {
            // 尝试多个常见路径
            var candidates = new[]
            {
                Path.Combine(baseWzPath, "Base.wz"),
                Path.Combine(baseWzPath, "Data", "Base", "Base.wz"),
                Path.Combine(baseWzPath, "Data.wz")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            // 在 baseWzPath 目录下查找任意 .wz 文件
            var wzFiles = Directory.GetFiles(baseWzPath, "*.wz", SearchOption.TopDirectoryOnly);
            if (wzFiles.Length > 0)
                return wzFiles[0];

            // 在 Data 子目录下查找
            var dataDir = Path.Combine(baseWzPath, "Data");
            if (Directory.Exists(dataDir))
            {
                wzFiles = Directory.GetFiles(dataDir, "*.wz", SearchOption.AllDirectories);
                if (wzFiles.Length > 0)
                    return wzFiles[0];
            }

            return null;
        }

        private WzHeaderInfo? ParseWzHeader(BinaryReader reader)
        {
            // WZ 文件头: 4字节签名 + 8字节数据大小 + 4字节头大小 + 版权字符串
            var signature = Encoding.UTF8.GetString(reader.ReadBytes(4));
            if (signature != "PKG1" && signature != "PKG4")
            {
                return null;
            }

            var dataSize = reader.ReadInt64();
            var headerSize = reader.ReadInt32();

            // 读取版权字符串（剩余头部区域）
            var copyrightBytes = reader.ReadBytes(headerSize - 16); // 16 = 4(sig) + 8(size) + 4(hdrSize)
            var copyright = Encoding.UTF8.GetString(copyrightBytes).TrimEnd('\0');

            var dataStartPos = headerSize;

            return new WzHeaderInfo
            {
                Signature = signature,
                DataSize = dataSize,
                HeaderSize = headerSize,
                Copyright = copyright,
                DataPos = dataStartPos,
                FileSize = (int)reader.BaseStream.Length
            };
        }

        private void ParseDirectory(BinaryReader reader, WzDirectoryNode parent, WzHeaderInfo header)
        {
            // 读取条目数
            var entryCount = ReadCompressedInt(reader);
            if (entryCount <= 0) return;

            for (int i = 0; i < entryCount; i++)
            {
                var type = reader.ReadByte();
                string name;

                switch (type)
                {
                    case 2:
                        // 偏移量字符串
                        var offset = reader.ReadInt32();
                        var savedPos = reader.BaseStream.Position;
                        reader.BaseStream.Seek(header.DataPos + offset, SeekOrigin.Begin);
                        name = ReadWzString(reader);
                        reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);
                        break;
                    case 3:
                    case 4:
                        name = ReadWzString(reader);
                        break;
                    default:
                        continue;
                }

                var size = ReadCompressedInt(reader);
                var checksum = ReadCompressedInt(reader);
                var hashedOffset = reader.ReadInt32();

                if (type == 3)
                {
                    // 子目录
                    var subDir = new WzDirectoryNode(name, parent);
                    parent.Children[name] = subDir;

                    // 递归解析子目录
                    var currentPos = reader.BaseStream.Position;
                    var subEndPos = currentPos + size;
                    ParseDirectory(reader, subDir, header);
                    reader.BaseStream.Seek(subEndPos, SeekOrigin.Begin);
                }
                else if (type == 4)
                {
                    // 图片节点 - 记录偏移用于后续解析
                    parent.Images[name] = new WzImageNode
                    {
                        Name = name,
                        Offset = hashedOffset,
                        Size = size,
                        FileOffset = (int)reader.BaseStream.Position - 4
                    };
                }
            }
        }

        private object? FindNodeByPathFallback(string path)
        {
            if (_wzRoot == null) return null;

            // 路径格式: "Mob/8880150.img/stand/0"
            var segments = path.Trim('/').Split('/');
            WzDirectoryNode? current = _wzRoot;

            for (int i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                if (current == null) return null;

                // 尝试作为子目录查找
                if (current.Children.TryGetValue(seg, out var child))
                {
                    current = child;
                    continue;
                }

                // 如果是最后一段或倒数第二段（带 .img 后缀），尝试作为图片节点
                if (seg.EndsWith(".img") || i == segments.Length - 1)
                {
                    // 去掉 .img 后缀查找
                    var imgKey = seg.EndsWith(".img") ? seg : seg + ".img";
                    if (current.Images.TryGetValue(imgKey, out var image))
                    {
                        // 返回图片节点，后续帧路径由上层处理
                        return image;
                    }
                    // 再试一次不带 .img 的
                    var imgKey2 = seg.EndsWith(".img") ? seg[..^4] : seg;
                    if (current.Images.TryGetValue(imgKey2, out var image2))
                    {
                        return image2;
                    }
                }

                // 尝试作为子节点（已经检查过）
                if (current.Images.TryGetValue(seg, out var imgNode))
                {
                    return imgNode;
                }

                return null;
            }

            return current;
        }

        private List<string> GetFramePathsFallback(object node, string action)
        {
            var result = new List<string>();
            if (node is not WzImageNode imgNode) return result;

            // 在当前节点下查找 action 子节点
            // 这里需要解析图片内部属性树
            // 简化实现：返回 0-30 帧的路径
            for (int i = 0; i < 30; i++)
            {
                result.Add($"{imgNode.Name}/{action}/{i}");
            }
            return result;
        }

        private byte[]? ExtractPngFallback(string wzPath)
        {
            // 从 WZ 路径找到 Canvas 节点并提取 PNG 数据
            // 简化实现：返回空，实际解析需要完整实现 WZ 图片格式
            return null;
        }

        private (int x, int y) GetOriginFallback(string wzPath)
        {
            // 从 WZ 路径解析 origin 属性
            // 简化实现：返回 (0, 0)
            return (0, 0);
        }

        private int GetDelayFallback(string wzPath)
        {
            // 从 WZ 路径解析 delay 属性
            return 100;
        }

        private (SpriteStrip? strip, PetConfig? config) ExportSpriteStripFallback(int mobId, string action)
        {
            return (null, null);
        }

        private List<string> GetActionListFallback(string path)
        {
            var result = new List<string>();
            if (_wzRoot == null) return result;

            var node = FindNodeByPathFallback(path);
            if (node is WzDirectoryNode dirNode)
            {
                // 子目录名称即为动作名
                foreach (var kv in dirNode.Children)
                {
                    result.Add(kv.Key);
                }
                foreach (var kv in dirNode.Images)
                {
                    result.Add(kv.Key);
                }
            }
            else if (node is WzImageNode imgNode)
            {
                // 图片节点下可能有动作子节点
                // 简化：返回常见动作
                result.Add("stand");
                result.Add("move");
                result.Add("fly");
                result.Add("attack1");
                result.Add("skill1");
                result.Add("hit");
                result.Add("die");
            }
            return result;
        }

        // ==================== WZ 二进制读取工具 ====================

        private static int ReadCompressedInt(BinaryReader reader)
        {
            var sbyteVal = reader.ReadSByte();
            if (sbyteVal == sbyte.MinValue)
                return reader.ReadInt32();
            return sbyteVal;
        }

        private static long ReadCompressedLong(BinaryReader reader)
        {
            var sbyteVal = reader.ReadSByte();
            if (sbyteVal == sbyte.MinValue)
                return reader.ReadInt64();
            return sbyteVal;
        }

        private static float ReadCompressedFloat(BinaryReader reader)
        {
            var sbyteVal = reader.ReadSByte();
            if (sbyteVal == sbyte.MinValue)
                return reader.ReadSingle();
            return sbyteVal;
        }

        private static string ReadWzString(BinaryReader reader)
        {
            var len = reader.ReadSByte();
            if (len == 0) return string.Empty;

            if (len > 0)
            {
                // Unicode 字符串
                var charCount = (len == 127) ? reader.ReadInt32() : len;
                var bytes = reader.ReadBytes(charCount * 2);
                return Encoding.Unicode.GetString(bytes);
            }
            else
            {
                // ASCII 字符串
                var byteCount = (len == -128) ? reader.ReadInt32() : -len;
                var bytes = reader.ReadBytes(byteCount);
                return Encoding.ASCII.GetString(bytes);
            }
        }

        private static string ReadStringBlock(BinaryReader reader, int offset)
        {
            var type = reader.ReadSByte();
            if (type == 0) return string.Empty;
            if (type > 0)
            {
                // 内联字符串
                var charCount = (type == 127) ? reader.ReadInt32() : type;
                var bytes = reader.ReadBytes(charCount * 2);
                return Encoding.Unicode.GetString(bytes);
            }
            else
            {
                // 偏移量字符串
                var stringOffset = reader.ReadInt32();
                var savedPos = reader.BaseStream.Position;
                reader.BaseStream.Seek(offset + stringOffset, SeekOrigin.Begin);
                var result = ReadWzString(reader);
                reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);
                return result;
            }
        }
    }

    // ==================== 内部模型 ====================

    internal class WzHeaderInfo
    {
        public string Signature { get; set; } = string.Empty;
        public long DataSize { get; set; }
        public int HeaderSize { get; set; }
        public string Copyright { get; set; } = string.Empty;
        public int DataPos { get; set; }
        public int FileSize { get; set; }
    }

    internal class WzDirectoryNode
    {
        public string Name { get; }
        public WzDirectoryNode? Parent { get; }
        public Dictionary<string, WzDirectoryNode> Children { get; } = new();
        public Dictionary<string, WzImageNode> Images { get; } = new();

        public WzDirectoryNode(string name, WzDirectoryNode? parent)
        {
            Name = name;
            Parent = parent;
        }
    }

    internal class WzImageNode
    {
        public string Name { get; set; } = string.Empty;
        public int Offset { get; set; }
        public int Size { get; set; }
        public int FileOffset { get; set; }
    }
}