using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        // Wz_Image.TryExtract 非线程安全：所有 img 提取串行化，避免并发损坏节点树
        private readonly object _wzLock = new();
        // 地图目录缓存（id→显示名）：后台构建、单飞、版本化
        private readonly object _mapCatalogLock = new();
        private Dictionary<string, string>? _mapNameCache;
        // 区域→街道→地图 三级层级（F6 级联筛选）：随目录后台构建，WZ 重载时置空重建
        private List<MapRegionNode>? _mapHierarchy;
        private int _mapCatalogGen;
        private Task? _mapCatalogTask;
        // LoadWz 并发守卫：多窗口同时触发加载时单飞
        private readonly object _loadLock = new();
        private bool _loading;

        public bool IsLoaded => _wzLoaded;
        public WzError? LastError => _lastError;

        public (bool success, WzError? error) LoadWz(string wzLibPath, string baseWzPath)
        {
            lock (_loadLock)
            {
                if (_loading) { Console.WriteLine("[WzService] LoadWz 进行中，跳过并发调用"); return (_wzLoaded, _lastError); }
                _loading = true;
            }
            try
            {
                _wzLoaded = false;
                _lastError = null;
                _mapHierarchy = null;

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
                LogTopLevelNodes();
                EnsureMapCatalog();
                return (true, null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] WZ 加载失败: {ex.Message}");
                _lastError = WzError.Unknown;
                return (false, _lastError);
            }
            }
            finally { lock (_loadLock) _loading = false; }
        }

        /// <summary>诊断：打印 WZ 顶层节点 + Character 子节点 + 关键纸娃娃路径是否存在。</summary>
        private void LogTopLevelNodes()
        {
            try
            {
                if (_wzs?.WzNode == null) return;
                var tops = new List<string>();
                foreach (Wz_Node n in _wzs.WzNode.Nodes) tops.Add(n.Text);
                Console.WriteLine($"[WzService] 顶层节点({tops.Count}): {string.Join(", ", tops)}");

                var charNode = _wzs.WzNode.Nodes.FirstOrDefault(n => n.Text == "Character");
                if (charNode == null)
                {
                    Console.WriteLine("[WzService] 诊断: Character 节点不存在!");
                }
                else
                {
                    var subs = new List<string>();
                    foreach (Wz_Node n in charNode.Nodes) subs.Add(n.Text);
                    Console.WriteLine($"[WzService] Character 子节点({subs.Count}): {string.Join(", ", subs.Take(40))}");
                    foreach (var probe in new[] { "Character/Hair", "Character/Face", "Character/Hair/00030000.img", "Character/Face/00020000.img", "Character/00002000.img", "Character/00012000.img" })
                        Console.WriteLine($"[WzService] 诊断 存在[{probe}]: {FindNodeByPath(probe) != null}");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] LogTopLevelNodes: {ex.Message}"); }
        }

        /// <summary>诊断：打印原始 piece 节点 + UOL/链接解析后目标，对比 origin/map/z/png 位置。</summary>
        public void DiagPiece(string path)
        {
            try
            {
                if (!_wzLoaded || _wzs?.WzNode == null) return;
                lock (_wzLock)
                {
                    var raw = _wzs.WzNode.FindNodeByPath(true, path.Replace('/', '\\').Split('\\'));
                    if (raw == null) { Console.WriteLine($"[WzDiag] {path}: raw null"); return; }
                    var rawKids = new List<string>();
                    foreach (Wz_Node c in raw.Nodes) rawKids.Add(c.Text);
                    Console.WriteLine($"[WzDiag] RAW {path}: value={raw.Value?.GetType().Name} kids=[{string.Join(",", rawKids.Take(12))}] origin={raw.FindNodeByPath("origin") != null} map={raw.FindNodeByPath("map") != null} z={raw.FindNodeByPath("z") != null}");

                    var uolNode = raw.ResolveUol() ?? raw;
                    if (uolNode != raw)
                        Console.WriteLine($"[WzDiag] UOL {path} -> {uolNode.FullPathToFile}: origin={uolNode.FindNodeByPath("origin") != null} map={uolNode.FindNodeByPath("map") != null} z={uolNode.FindNodeByPath("z") != null} png={uolNode.GetValue<Wz_Png>() != null}");

                    var tgt = ResolveToCanvas(path);
                    if (tgt == null || tgt == raw) { Console.WriteLine($"[WzDiag] target=raw（无解析）"); return; }
                    var tgtKids = new List<string>();
                    foreach (Wz_Node c in tgt.Nodes) tgtKids.Add(c.Text);
                    Console.WriteLine($"[WzDiag] TGT {tgt.FullPathToFile}: value={tgt.Value?.GetType().Name} kids=[{string.Join(",", tgtKids.Take(12))}] origin={tgt.FindNodeByPath("origin") != null} map={tgt.FindNodeByPath("map") != null} z={tgt.FindNodeByPath("z") != null} png={tgt.GetValue<Wz_Png>() != null}");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzDiag] {path}: {ex.Message}"); }
        }

        public Wz_Node? FindNodeByPath(string path)
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            lock (_wzLock)
            {
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
        /// 解析路径到最终 canvas 节点：先 ResolveUol（Wz_Uol 链接），再 source/_inlink/_outlink。
        /// 角色 head/hair/装备等大量 piece 是 Wz_Uol，必须先解 UOL 才能拿到真实 canvas。
        /// </summary>
        private Wz_Node? ResolveToCanvas(string wzPath)
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            lock (_wzLock)
            {
                try
                {
                    var node = _wzs.WzNode.FindNodeByPath(true, wzPath.Replace('/', '\\').Split('\\'));
                    if (node == null) return null;
                    node = node.ResolveUol() ?? node;
                    return GetLinkedSourceNode(node) ?? node;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WzService] ResolveToCanvas({wzPath}): {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// 解析 UOL 后的节点（不再跳 _outlink/_inlink）：head/hair 等 piece 是 Wz_Uol，
        /// 其 UOL 目标节点自带 origin/map/z/png；继续 _outlink 跳到 _Canvas 会丢这些元数据。
        /// </summary>
        private Wz_Node? ResolveUolNode(string wzPath)
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            lock (_wzLock)
            {
                try
                {
                    var node = _wzs.WzNode.FindNodeByPath(true, wzPath.Replace('/', '\\').Split('\\'));
                    return node?.ResolveUol() ?? node;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WzService] ResolveUolNode({wzPath}): {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// 获取帧 PNG 数据
        /// </summary>
        public byte[]? ExtractPng(string wzPath)
        {
            try
            {
                var node = ResolveToCanvas(wzPath);
                if (node == null) return null;
                var png = node.GetValue<Wz_Png>();
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
                // origin 在 canvas 节点上：先解 UOL/链接再读
                // origin 优先取原始节点（_outlink 时 origin 在源节点上，PNG 才在 _Canvas 目标），取不到再回退 canvas
                var origin = ResolveUolNode(wzPath)?.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null)
                    ?? ResolveToCanvas(wzPath)?.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
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
        /// 读取某帧下所有 piece（canvas 子节点）名，用于纸娃娃逐件合成。
        /// </summary>
        public List<string> GetFramePieceNames(string framePath)
        {
            var result = new List<string>();
            try
            {
                var node = FindNodeByPath(framePath);
                if (node == null) return result;
                foreach (Wz_Node child in node.Nodes)
                    result.Add(child.Text);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetFramePieceNames({framePath}): {ex.Message}"); }
            return result;
        }

        /// <summary>
        /// 读取 piece 节点的 map 锚点表（map/{anchorName} → Wz_Vector）。
        /// </summary>
        public Dictionary<string, (int x, int y)> GetPieceMap(string piecePath)
        {
            var result = new Dictionary<string, (int x, int y)>();
            try
            {
                // map 锚点表优先取原始节点（_outlink 时 map 在源节点上），取不到再回退 canvas
                var mapNode = ResolveUolNode(piecePath)?.FindNodeByPath("map")
                    ?? ResolveToCanvas(piecePath)?.FindNodeByPath("map");
                if (mapNode == null) return result;
                foreach (Wz_Node child in mapNode.Nodes)
                {
                    var v = child.GetValueEx<Wz_Vector>(null);
                    if (v != null) result[child.Text] = (v.X, v.Y);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetPieceMap({piecePath}): {ex.Message}"); }
            return result;
        }

        /// <summary>读取 piece 的 z 字段（层名字符串，可能为 null/数字）。</summary>
        public string? GetZField(string piecePath)
        {
            try
            {
                // z 字段优先取原始节点（_outlink 时 z 在源节点上），取不到再回退 canvas
                var zNode = ResolveUolNode(piecePath)?.FindNodeByPath("z")
                    ?? ResolveToCanvas(piecePath)?.FindNodeByPath("z");
                return zNode?.GetValueEx<string>(null) ?? zNode?.Value?.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// 读取 Base/zmap.img/zmap 的层名顺序（index 0 = 最顶层）。失败返回空。
        /// </summary>
        public List<string> GetZmapOrder()
        {
            var result = new List<string>();
            try
            {
                if (!_wzLoaded || _wzs?.WzNode == null) return result;
                foreach (var top in new[] { "Base/zmap.img/zmap", "zmap.img/zmap", "Base/zmap.img" })
                {
                    var node = FindNodeByPath(top);
                    if (node == null) continue;
                    foreach (Wz_Node child in node.Nodes)
                    {
                        var name = child.GetValueEx<string>(null) ?? child.Value?.ToString();
                        if (!string.IsNullOrEmpty(name)) result.Add(name);
                    }
                    if (result.Count > 0) break;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetZmapOrder: {ex.Message}"); }
            return result;
        }

        /// <summary>
        /// 从 String.wz 获取怪物名称  String/Mob.img/{mobId}/name
        /// </summary>
        public string GetMobName(string mobId)
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
        public string GetNpcName(string npcId)
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

        /// <summary>把数字 id 字符串补零到指定位数（等价于 int 的 D7/D8 格式）</summary>
        private static string PadId(string id, int width) => (id ?? string.Empty).PadLeft(width, '0');

        /// <summary>
        /// 导出精灵图条和配置，支持 Mob/NPC 类型
        /// </summary>
        public (SpriteStrip? strip, PetConfig? config) ExportSpriteStrip(string id, string action, string type = "Mob")
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return (null, null);
            lock (_wzLock)
            try
            {
                // Meta: {type}/{id}.img — 含 origin/delay/a0/a1
                var metaImgNode = _wzs.WzNode.FindNodeByPath(true, $"{type}/{PadId(id, 7)}.img".Split('/'));
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
                    // 跳过无效帧：null 或 1×1 退化占位（WZ 里部分空动作帧无有效画面），与 BuildSpriteStripPng 一致
                    if (png == null || png.Width <= 1 || png.Height <= 1) continue;
                    int fw = png.Width, fh = png.Height;

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
        public (SpriteStrip? strip, PetConfig? config) ExportSpriteStrip(string mobId, string action) => ExportSpriteStrip(mobId, action, "Mob");

        /// <summary>
        /// 从 WZ 提取精灵图条 PNG（拼接所有帧为水平条带），支持 Mob/NPC
        /// </summary>
        public byte[]? BuildSpriteStripPng(string id, string action, string type = "Mob")
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            lock (_wzLock)
            try
            {
                // Meta: {type}/{id}.img — origin 在 child 上，PNG 通过 child outlink 取
                var metaImgNode = _wzs.WzNode.FindNodeByPath(true, $"{type}/{PadId(id, 7)}.img".Split('/'));
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
                    // 跳过无效帧：null 或 1×1 退化占位（WZ 里部分空动作帧无有效画面），避免合成出空白条带
                    if (png == null || png.Width <= 1 || png.Height <= 1) continue;

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
        public byte[]? BuildSpriteStripPng(string mobId, string action) => BuildSpriteStripPng(mobId, action, "Mob");

        /// <summary>
        /// 获取动作列表 — 支持 Mob/NPC 类型，从含动作元数据的 {type}/{id}.img 枚举动作节点
        /// </summary>
        public List<string> GetActionList(string id, string type = "Mob")
        {
            var result = new List<string>();
            lock (_wzLock)
            try
            {
                if (!_wzLoaded || _wzs?.WzNode == null) return result;

                var imgNode = _wzs.WzNode.FindNodeByPath(true, $"{type}/{PadId(id, 7)}.img".Split('/'));
                if (imgNode == null) return result;
                var img = imgNode.GetValue<Wz_Image>();
                if (img == null || !img.TryExtract()) return result;

                foreach (Wz_Node child in img.Node.Nodes)
                {
                    if (int.TryParse(child.Text, out _)) continue;
                    // 仅列入「有有效画面」的动作：至少一帧能取到 >1×1 的 PNG，排除 WZ 里的空动作
                    if (HasValidFramePng(child))
                        result.Add(child.Text);
                }

                result = result.Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] GetActionList({id}/{type}): {ex.Message}");
            }
            return result;
        }

        /// <summary>动作节点是否至少有一帧能取到有效 PNG（>1×1），用于排除 WZ 里无画面的空动作。</summary>
        private static bool HasValidFramePng(Wz_Node actionNode)
        {
            foreach (Wz_Node frame in actionNode.Nodes)
            {
                if (!int.TryParse(frame.Text, out _)) continue;
                var pngNode = frame.ResolveUol() ?? frame;
                var linkNode = GetLinkedSourceNode(pngNode) ?? pngNode;
                var png = linkNode.GetValue<Wz_Png>() ?? pngNode.GetValue<Wz_Png>();
                if (png != null && png.Width > 1 && png.Height > 1) return true;
            }
            return false;
        }

        // 保留旧签名兼容
        public List<string> GetActionList(string mobId) => GetActionList(mobId, "Mob");

        public Dictionary<string, List<string>> AutoDiscoverMappings(string mobId)
        {
            var result = new Dictionary<string, List<string>>();
            var actions = GetActionList(mobId);
            foreach (var action in actions)
            {
                result[action] = new List<string>();

                var node = FindNodeByPath($"Mob/_Canvas/{PadId(mobId, 7)}.img/{action}");
                if (node == null) continue;
                for (int f = 0; ; f++)
                {
                    if (node.FindNodeByPath(f.ToString()) == null) break;
                    result[action].Add($"Mob/{mobId}.img/{action}/{f}");
                }
            }
            return result;
        }

        public string GetCharacterPartWzPath(string partType, string itemId, string action, int frame)
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
            return $"Character/{category}/{PadId(itemId, 8)}.img/{action}/{frame}";
        }

        public string GetMapWzPath(string mapId)
        {
            var prefix = !string.IsNullOrEmpty(mapId) ? mapId[0] : '0';
            return $"Map/Map/Map{prefix}/{mapId}.img";
        }

        public string GetMobWzPath(string mobId, string action, int frame) =>
            $"Mob/_Canvas/{PadId(mobId, 7)}.img/{action}/{frame}";

        public string GetNpcWzPath(string npcId, string action, int frame) =>
            $"Npc/_Canvas/{PadId(npcId, 7)}.img/{action}/{frame}";

        public string GetBalloonTilePath(string balloonId, string tileName) =>
            $"UI/ChatBalloon.img/{balloonId}/{tileName}";

        /// <summary>
        /// 取 UI/ChatBalloon.img/{id} 节点（提取后），用于读 9-slice 切片和 clr
        /// </summary>
        public Wz_Node? GetBalloonIdNode(string id)
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            lock (_wzLock)
            try
            {
                var imgNode = _wzs.WzNode.FindNodeByPath(true, "UI/ChatBalloon.img".Split('/'));
                var img = imgNode?.GetValue<Wz_Image>();
                if (img == null || !img.TryExtract()) return null;
                return img.Node.FindNodeByPath(id);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetBalloonIdNode({id}): {ex.Message}"); return null; }
        }

        /// <summary>
        /// 提取聊天气泡的 9-slice 切片（nw/n/head/ne/w/c/e/sw/s/arrow/se），返回 PNG 字节
        /// </summary>
        public BalloonTiles? ExtractBalloonTiles(string id)
        {
            try
            {
                var node = GetBalloonIdNode(id);
                if (node == null) return null;
                return new BalloonTiles
                {
                    NW = ExtractTilePng(node, "nw"),
                    N = ExtractTilePng(node, "n"),
                    NE = ExtractTilePng(node, "ne"),
                    W = ExtractTilePng(node, "w"),
                    C = ExtractTilePng(node, "c"),
                    E = ExtractTilePng(node, "e"),
                    SW = ExtractTilePng(node, "sw"),
                    S = ExtractTilePng(node, "s"),
                    SE = ExtractTilePng(node, "se"),
                    Arrow = ExtractTilePng(node, "arrow"),
                    Head = ExtractTilePng(node, "head")
                };
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] ExtractBalloonTiles({id}): {ex.Message}"); return null; }
        }

        /// <summary>
        /// 读聊天气泡字体颜色 clr（有符号 ARGB，常为负数如 -1=白）；缺失返回 -1（白）
        /// </summary>
        public int GetBalloonClr(string id)
        {
            try
            {
                var node = GetBalloonIdNode(id);
                return node?.FindNodeByPath("clr")?.GetValueEx<int>(-1) ?? -1;
            }
            catch { return -1; }
        }

        private static byte[] ExtractTilePng(Wz_Node parent, string name)
        {
            try
            {
                var n = parent.FindNodeByPath(name);
                var png = n?.GetValue<Wz_Png>();
                if (png == null) return Array.Empty<byte>();
                return PngEncoder.Encode(PngEncoder.DecodePixels(png), png.Width, png.Height);
            }
            catch { return Array.Empty<byte>(); }
        }

        public List<(string Id, string Name)> GetDirectoryChildren(string wzDirectory)
        {
            var result = new List<(string Id, string Name)>();
            try
            {
                var node = FindNodeByPath($"{wzDirectory}/_Canvas");
                if (node == null) return result;
                foreach (Wz_Node child in node.Nodes)
                {
                    var key = child.Text;
                    if (!key.EndsWith(".img")) continue;
                    var cleanKey = key[..^4];
                    if (int.TryParse(cleanKey, out var idNum))
                        result.Add((idNum.ToString(), key)); // 无前导零，匹配 String.wz 键
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] GetDirectoryChildren({wzDirectory}): {ex.Message}");
            }
            return result;
        }

        public List<string> ListMobIds() =>
            GetDirectoryChildren("Mob").Select(x => x.Id).Where(id => !string.IsNullOrEmpty(id) && id != "0").ToList();

        public List<string> ListNpcIds() =>
            GetDirectoryChildren("Npc").Select(x => x.Id).Where(id => !string.IsNullOrEmpty(id) && id != "0").ToList();

        /// <summary>
        /// 枚举 WZ 全量地图 id：遍历 Map/Map/Map{0..9} 下所有 .img，文件名去 .img = 9 位地图 id。持 _wzLock。
        /// </summary>
        public List<string> ListMapIds()
        {
            var result = new List<string>();
            if (!_wzLoaded || _wzs?.WzNode == null) return result;
            lock (_wzLock)
            {
                try
                {
                    var mapMapNode = _wzs.WzNode.FindNodeByPath(true, "Map", "Map");
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
                catch (Exception ex) { Console.Error.WriteLine($"[WzService] ListMapIds: {ex.Message}"); }
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
            if (!_wzLoaded || _wzs?.WzNode == null) return cache;
            lock (_wzLock)
            {
                try
                {
                    var imgNode = _wzs.WzNode.FindNodeByPath(true, "String", "Map.img");
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
                catch (Exception ex) { Console.Error.WriteLine($"[WzService] BuildMapNameCache: {ex.Message}"); }
            }
            return cache;
        }

        /// <summary>在 _wzLock 内执行操作（可重入）：供 MapService.LoadMap 等外部消费者把整段节点遍历串行化。</summary>
        public T WithWzLock<T>(Func<T> action)
        {
            lock (_wzLock) return action();
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
            if (!_wzLoaded) return;
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
                            Console.WriteLine($"[WzService] 地图目录构建完成: {built.Count} 个");
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
            if (!_wzLoaded || _wzs?.WzNode == null) return regions;
            lock (_wzLock)
            {
                try
                {
                    var imgNode = _wzs.WzNode.FindNodeByPath(true, "String", "Map.img");
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
                catch (Exception ex) { Console.Error.WriteLine($"[WzService] BuildMapHierarchy: {ex.Message}"); }
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

    /// <summary>地图区域节点（层级：区域 → 街道 → 地图），供背景设置 tab 级联筛选。见 WzService.GetMapHierarchy。</summary>
    public sealed class MapRegionNode
    {
        public string Name { get; set; } = string.Empty;
        public List<MapStreetNode> Streets { get; set; } = new();
    }

    /// <summary>地图街道节点（同级若干地图，地图项复用 Models.MapEntry）。</summary>
    public sealed class MapStreetNode
    {
        public string Name { get; set; } = string.Empty;
        public List<MapEntry> Maps { get; set; } = new();
    }
}
