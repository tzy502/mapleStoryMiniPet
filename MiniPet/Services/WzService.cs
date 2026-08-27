using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
        // Mob 完整信息源：Data/Mob/Mob.wz 文件模式加载（Mob/{id}.img 含动作/origin/PNG-outlink；_Canvas 只是 PNG 仓库，不直接找）
        private Wz_Node? _mobFullRoot;
        private WzError? _lastError;
        // Wz_Image.TryExtract 非线程安全：所有 img 提取串行化，避免并发损坏节点树
        private readonly object _wzLock = new();
        // D8/D9 结果缓存（GetItemName / GetBalloonList）：WZ 重载时清空（见 LoadWz）。
        // 锁纪律：_cacheLock 与 _wzLock 不嵌套逆序——取/存缓存不持锁调用 WZ（见各方法），
        // 唯一同时持两锁的路径是 GetBalloonList 的 _wzLock → _cacheLock 顺序，无死锁。
        private readonly object _cacheLock = new();
        private readonly Dictionary<(string Category, string Id), string> _itemNameCache = new();
        private List<(int Id, string Name)>? _balloonListCache;
        // 地图目录服务（God Class 拆分：地图 id 枚举/名称缓存/三级层级 独立成类）
        private readonly MapCatalogService _mapCatalog;
        // LoadWz 并发守卫：多窗口同时触发加载时单飞
        private readonly object _loadLock = new();
        private bool _loading;

        public WzService()
        {
            _mapCatalog = new MapCatalogService(this);
        }

        public bool IsLoaded => _wzLoaded;
        public WzError? LastError => _lastError;
        public string? LastWzLibWarning { get; private set; }

        /// <summary>
        /// WZ 重载代际失效钩子（V0.2.0 音乐模块）：每次 LoadWz 尝试收场（成功或失败）后触发一次，
        /// 携带自增后的重载代际号（订阅方用它区分「哪一代 WZ」——目录任务启动记代际、完成后比对，不符即失效）。
        /// 供 MusicCatalogService 等订阅方清缓存/失效在途任务。⚠️ 触发点绝不持有 _wzLock/_loadLock——
        /// 订阅方回调可能反向调用 WzService，持锁触发会死锁（见 LoadWz finally）。
        /// </summary>
        public event Action<int>? WzReloaded;

        /// <summary>WZ 重载代际号：每次 LoadWz 尝试收场时自增（随 WzReloaded 事件发布）。</summary>
        private int _wzGeneration;

        /// <summary>WzError → 中文文案映射，供失败弹窗/状态栏使用。</summary>
        public static string GetErrorText(WzError err) => err switch
        {
            WzError.DllNotFound => "wzlib DLL 文件不存在",
            WzError.DllLoadFailed => "wzlib DLL 加载失败",
            WzError.WzPathInvalid => "BaseWZ 路径无效",
            WzError.WzFileCorrupted => "WZ 文件损坏",
            WzError.VersionMismatch => "WZ 版本不兼容",
            _ => "未知错误"
        };

        // MapCatalogService 内部访问器（组合服务需要访问 WZ 根节点与加载态）
        internal bool IsWzLoaded => _wzLoaded;
        internal Wz_Node? WzRoot => _wzs?.WzNode;
        internal object WzLock => _wzLock;

        public (bool success, WzError? error) LoadWz(string wzLibPath, string baseWzPath)
        {
            lock (_loadLock)
            {
                if (_loading) { Console.WriteLine("[WzService] LoadWz 进行中，跳过并发调用"); return (_wzLoaded, _lastError); }
                _loading = true;
            }
            try
            {
                // 重载开始即在锁内发布「卸载」状态（原为锁外直接置 _wzLoaded = false，行为等价、可见性更强）：
                // 后续构建全部在局部变量上进行，读者构建期间见「未加载」，不再进入旧结构
                lock (_wzLock)
                {
                    _wzLoaded = false;
                }
                _lastError = null;
                LastWzLibWarning = null;
                _mapCatalog.Reset();
                // D8/D9：WZ 重载/重建后结果缓存失效（新数据可能改变名称/气泡集合）
                lock (_cacheLock)
                {
                    _itemNameCache.Clear();
                    _balloonListCache = null;
                }

                // wzlib 运行时状态：WzLibRuntimeLoader 已在启动早期按用户配置加载用户 DLL（无效/不兼容自动回退内置），
                // 此处仅同步提示状态到 UI（LoadWz 的 wzLibPath 参数不再影响 DLL 选择，加载发生在进程启动时）
                LastWzLibWarning = WzLibRuntimeLoader.LastWarning;

            if (string.IsNullOrEmpty(baseWzPath) || !Directory.Exists(baseWzPath))
            {
                _lastError = WzError.WzPathInvalid;
                // D1：路径无效视为加载失败——锁内发布置空状态（释放上一轮残留结构，避免半构建内存驻留）
                lock (_wzLock)
                {
                    _wzs = null;
                    _mobFullRoot = null;
                }
                return (false, _lastError);
            }
            _baseWzPath = baseWzPath;

            // 局部构建变量：全部加载（含 Mob/Packs 追加）完成后才在单个 _wzLock 临界区一次性发布，
            // 消除原「先行置 _wzLoaded=true 后仍继续加载至 Packs 完成」的半成品发布窗口
            Wz_Structure wzs;
            Wz_Node? mobFullRoot = null;
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
                wzs = new Wz_Structure();
                wzs.LoadWzFolder(wzFolder, ref wzs.WzNode, true);
                Console.WriteLine($"[WzService] Base 加载成功, imgs: {wzs.img_number}");

                // Mob 完整信息源：Data/Mob/Mob.wz 文件模式（loadWzAsFolder=false——完整 img：Mob/{id}.img 含动作/origin/PNG-outlink）
                mobFullRoot = null;
                var mobDir = Path.Combine(baseWzPath, "Mob");
                if (Directory.Exists(mobDir) && File.Exists(Path.Combine(mobDir, "Mob.wz")))
                {
                    try
                    {
                        var mobWzs = new Wz_Structure();
                        Wz_Structure.DefaultAutoDetectExtFiles = true;
                        Wz_Structure.DefaultImgCheckDisabled = true;
                        var mobRoot = new Wz_Node("Mob");
                        mobWzs.LoadFile(Path.Combine(mobDir, "Mob.wz"), mobRoot, false, false);
                        mobFullRoot = mobRoot;
                        Console.WriteLine($"[WzService] Mob.wz 文件模式加载成功, 根节点: {mobRoot.Nodes.Count}");
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[WzService] Mob.wz 文件模式失败: {ex.Message}"); mobFullRoot = null; }
                }

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
                            wzs.LoadMsFile(msFile);
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
                            wzs.LoadMsFile(mnFile);
                            Console.WriteLine($"[WzService] Packs 加载: {Path.GetFileName(mnFile)}");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WzService] Packs 加载失败 {mnFile}: {ex.Message}");
                        }
                    }
                }
                Console.WriteLine($"[WzService] 加载完成, imgs: {wzs.img_number}");
                // 单临界区发布：_wzs / _mobFullRoot / _wzLoaded 一次性原子可见——
                // 读者要么见完整新结构（已加载），要么见未加载，不再观察到半成品结构
                lock (_wzLock)
                {
                    _wzs = wzs;
                    _mobFullRoot = mobFullRoot;
                    _wzLoaded = true;
                }
                LogTopLevelNodes();
                EnsureMapCatalog();
                return (true, null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] WZ 加载失败: {ex.Message}");
                _lastError = WzError.Unknown;
                // D1：释放半构建结构（内存驻留 + 允许下次 LoadWz 干净重试）——锁内发布置空状态
                lock (_wzLock)
                {
                    _wzs = null;
                    _mobFullRoot = null;
                    _wzLoaded = false;
                }
                return (false, _lastError);
            }
            }
            finally
            {
                lock (_loadLock) _loading = false;
                // 重载代际失效钩子：每次 LoadWz 尝试收场（成功或失败）后触发一次，供 MusicCatalogService
                // 等订阅方清缓存。⚠️ 绝不可在持 _wzLock/_loadLock 时触发——订阅方回调可能反向调用
                // WzService（持锁触发会死锁），故置于 finally 且在两锁释放之后。
                int gen = Interlocked.Increment(ref _wzGeneration);
                try { WzReloaded?.Invoke(gen); }
                catch (Exception ex) { Console.Error.WriteLine($"[WzService] WzReloaded: {ex.Message}"); }
            }
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
        /// 提取 Sound.wz 里的一首音乐（MP3 字节流，V0.2.0 音乐模块）。
        /// imgName 如 "Bgm00.img"，trackName 如 "SleepyWood"。
        /// 全程 _wzLock 锁串行化；判空检查同样在锁内（Rev 4：避免「锁外见已加载、进锁后已被重载置空」的竞态）。
        /// ⚠️ WZ 路径必须带 Sound/ 前缀（探针实证：BgmXX.img 在 Sound 节点下，根节点找不到）；
        /// WzLib 单参 FindNodeByPath 按 '\\' 分割路径，正斜杠需 Replace（与上方 FindNodeByPath 同套路）。
        /// </summary>
        /// <param name="imgName">音乐分类文件名，如 "Bgm00.img"。</param>
        /// <param name="trackName">曲名（img 内 Wz_Sound 子节点名），如 "SleepyWood"。</param>
        /// <returns>MP3 字节流；未加载/找不到曲目/提取失败返回 null。</returns>
        public byte[]? ExtractSound(string imgName, string trackName)
        {
            lock (_wzLock)
            {
                if (!_wzLoaded || _wzs?.WzNode == null)
                {
                    return null;
                }
                try
                {
                    var imgNode = _wzs.WzNode.FindNodeByPath(("Sound/" + imgName).Replace('/', '\\'));
                    if (imgNode == null)
                    {
                        return null;
                    }
                    var img = imgNode.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract())
                    {
                        return null;
                    }
                    // ⚠️ 曲目节点必须读 img.Node.Nodes（Wz_Image.Node 属性）——TryExtract 后外层 node.Nodes 恒 0
                    var trackNode = img.Node.Nodes.FirstOrDefault(n => n.Text == trackName);
                    if (trackNode == null)
                    {
                        return null;
                    }
                    var sound = trackNode.GetValue<Wz_Sound>();
                    return sound?.ExtractSound();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WzService] ExtractSound({imgName}/{trackName}): {ex.Message}");
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
                    // R14：UOL 指向帧目录而非 canvas 时，按源 piece 名下钻（如 1103058 heal/0/cape → ../../alert/1 → alert/1/cape）
                    node = DescendToPieceNode(node, wzPath);
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
                    var resolved = node?.ResolveUol() ?? node;
                    // R14：同上——UOL 目标为帧目录时按 piece 名下钻取真实 canvas（origin/map/z 在 canvas 上）
                    return DescendToPieceNode(resolved, wzPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WzService] ResolveUolNode({wzPath}): {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// R14：UOL 解析结果若为目录（帧节点，无 Wz_Png）且源 piece 名是其子节点，则下钻到该子节点。
        /// 部分披风帧的 UOL 指向帧节点而非 canvas（1103058 heal/0/cape → ../../alert/1，canvas 为 alert/1/cape），
        /// 不下钻则 origin/map/z/PNG 全缺失 → 该帧披风不渲染。仅目录目标触发，canvas 目标原样返回，无回归风险。
        /// </summary>
        private static Wz_Node? DescendToPieceNode(Wz_Node? node, string wzPath)
        {
            if (node == null) return null;
            if (node.GetValue<Wz_Png>() != null) return node;
            string pieceName = wzPath.Replace('/', '\\').Split('\\').LastOrDefault() ?? "";
            if (string.IsNullOrEmpty(pieceName)) return node;
            var child = node.FindNodeByPath(pieceName);
            if (child != null && (child.GetValue<Wz_Png>() != null || child.Value is Wz_Uol))
            {
                return child;
            }
            return node;
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
        /// 轻量尺寸查询（2026-08-16 冰凌披风特效卡顿优化）：只读 Wz_Png 的 Width/Height，
        /// 不执行 DecodePixels/Encode（ExtractPng 全量编码 + SKBitmap.Decode 是特效 155x140 大图
        /// 首次加载卡顿的根因——GetBounds 只需要尺寸却走了全量解码）。解析失败返回 null。
        /// </summary>
        public (int W, int H)? GetPngSize(string wzPath)
        {
            try
            {
                var node = ResolveToCanvas(wzPath);
                if (node == null) return null;
                var png = node.GetValue<Wz_Png>();
                if (png == null) return null;
                return (png.Width, png.Height);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] GetPngSize({wzPath}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 提取装备/部件图标 PNG（素材浏览器 icon 预览用）：优先 {root}/icon，回退 {root}/iconRaw。
        /// 节点为 Wz_Png 或经 UOL/_outlink 链接（解析链与 ExtractTilePng 一致）；锁语义同其他提取方法（_wzLock 内串行化）。
        /// 返回 PNG 编码字节；无图标/解析失败返回 null。
        /// </summary>
        /// <param name="root">WZ 部件路径（不含字段名），如 Character/Cap/01000000.img。</param>
        public byte[]? GetIcon(string root)
        {
            try
            {
                if (!_wzLoaded || _wzs?.WzNode == null || string.IsNullOrEmpty(root)) return null;
                lock (_wzLock)
                {
                    foreach (var field in new[] { "icon", "iconRaw" })
                    {
                        var node = _wzs.WzNode.FindNodeByPath(true, $"{root}/{field}".Replace('/', '\\').Split('\\'));
                        if (node == null) continue;
                        try
                        {
                            var pngNode = node.ResolveUol() ?? node;
                            var linkNode = GetLinkedSourceNode(pngNode) ?? pngNode;
                            var png = linkNode.GetValue<Wz_Png>() ?? pngNode.GetValue<Wz_Png>();
                            if (png == null || png.Width <= 1 || png.Height <= 1) continue;
                            return PngEncoder.Encode(PngEncoder.DecodePixels(png), png.Width, png.Height);
                        }
                        catch { /* 单字段解析失败 → 试下一字段 */ }
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] GetIcon({root}): {ex.Message}");
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
                return node.FindNodeByPath("delay")?.GetValueEx<int>(120) ?? 120;
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

        /// <summary>读取纸娃娃 body 帧的 navel 绝对位置（MapleSalon2 容器原点 = body/origin + body/map/navel；stand1 首帧固定基准）。</summary>
        public (int x, int y)? GetPaperdollNavel(string bodyImg = "00002000.img")
        {
            try
            {
                return WithWzLock<(int, int)?>(() =>
                {
                    var img = FindNodeByPath($"Character/{bodyImg}")?.GetValue<Wz_Image>();
                    img?.TryExtract();
                    var body = img?.Node.FindNodeByPath(true, "stand1", "0", "body");
                    var origin = body?.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
                    var navel = body?.FindNodeByPath("map")?.FindNodeByPath("navel")?.GetValueEx<Wz_Vector>(null);
                    if (origin == null || navel == null) return null;
                    return (origin.X + navel.X, origin.Y + navel.Y);
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetPaperdollNavel: {ex.Message}"); return null; }
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
                // 桌宠 WZ：顶层独立 zmap.img / zmap_cn.img（Wz_Image 需 TryExtract），无 Base/zmap.img
                foreach (var top in new[] { "Base/zmap.img", "zmap.img", "zmap_cn.img" })
                {
                    var imgNode = FindNodeByPath(top);
                    Console.WriteLine($"[WzService] GetZmapOrder: top={top} node={(imgNode == null ? "NULL" : imgNode.Text)}");
                    if (imgNode == null) continue;
                    var img = imgNode.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract())
                    {
                        Console.WriteLine($"[WzService] GetZmapOrder: {top} 提取失败/非 img");
                        continue;
                    }
                    Console.WriteLine($"[WzService] GetZmapOrder: {top} 顶层子节点前30=[{string.Join(",", img.Node.Nodes.Take(30).Select(x => x.Text))}]");
                    // 层名 = zmap.img 顶层子节点的 Text（mobEquipFront→…→accessoryEyeOverCap，底→顶）
                    foreach (Wz_Node child in img.Node.Nodes)
                    {
                        var name = child.Text;
                        if (!string.IsNullOrEmpty(name)) result.Add(name);
                    }
                    if (result.Count > 0)
                    {
                        Console.WriteLine($"[WzService] GetZmapOrder: 从 {top} 读到 {result.Count} 层, 前5=[{string.Join(",", result.Take(5))}] 后5=[{string.Join(",", result.Skip(Math.Max(0, result.Count - 5)))}]");
                        break;
                    }
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

        /// <summary>
        /// 从 String.wz 获取纸娃娃部件中文名（发型/脸型/装备/坐骑/皮肤）。
        /// 桌宠 WZ 探针：戒指名在 String/Eqp.img/Eqp/Ring/{code}/name（Wz_Image 需 TryExtract）。
        /// 多路径探测：Item.img/{cat}/{key}、Item.img/{cat}/{8位}、Eqp.img 各种层级 + TryExtract 回退。
        /// D8：结果缓存（(category,id)→name，含空结果），WZ 重载清空——避免每次调用探测 9 条候选路径。
        /// </summary>
        public string GetItemName(string category, string id)
        {
            lock (_cacheLock)
            {
                if (_itemNameCache.TryGetValue((category, id), out var cached))
                {
                    return cached;
                }
            }
            var result = GetItemNameCore(category, id);
            lock (_cacheLock)
            {
                _itemNameCache[(category, id)] = result;
            }
            return result;
        }

        private string GetItemNameCore(string category, string id)
        {
            try
            {
                var key = id.TrimStart('0');
                if (string.IsNullOrEmpty(key)) key = "0";
                var pad = PadId(id, 8);
                // 皮肤专用：String Skin 分类 id = Body id + 10000（12000 系列；MapleSalon2 规则实测，
                // String/Eqp.img/Eqp 分类表无 Body 只有 Skin，2000 系列直接查永远落空 → 皮肤名全 fallback「皮肤 N」）
                if (string.Equals(category, "Body", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(key, out var skinBodyId))
                {
                    var skinKey = (skinBodyId + 10000).ToString();
                    foreach (var p in new[]
                    {
                        $"String/Eqp.img/Eqp/Skin/{skinKey}",
                        $"String/Item.img/{category}/{skinKey}",
                    })
                    {
                        var n = FindNodeByPath($"{p}/name")?.GetValueEx<string>(null);
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                }
                var candidates = new[]
                {
                    $"String/Item.img/{category}/{key}",
                    $"String/Item.img/{category}/{pad}",
                    $"String/Ins.img/{key}",
                    $"String/Ins.img/{pad}",
                    // 坐骑专用：String 分类名是 Taming 不是 TamingMob（2026-08-16 probe 实测
                    // String/Eqp.img/Eqp/Taming/1902000 = 멧돼지/银色野猪…，原候选全落空 → 坐骑名全 fallback「坐骑_N」）
                    $"String/Eqp.img/Eqp/Taming/{key}",
                    $"String/Eqp.img/Eqp/{category}/{key}",
                    $"String/Eqp.img/{category}/{key}",
                    $"String/Eqp.img/Eqp/{key}",
                    $"String/Eqp.img/{key}",
                    $"String/Etc.img/{key}",
                    $"String/Etc.img/{category}/{key}",
                    $"String/Consume.img/{key}",
                };
                foreach (var p in candidates)
                {
                    var n = FindNodeByPath($"{p}/name")?.GetValueEx<string>(null);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
                // Eqp.img 是 Wz_Image：TryExtract 后走内部节点
                var eqpImg = FindNodeByPath("String/Eqp.img");
                var img = eqpImg?.GetValue<Wz_Image>();
                if (img != null && img.TryExtract())
                {
                    foreach (var p in new[] { $"Eqp/{category}/{key}", $"{category}/{key}", $"Eqp/{key}", $"{key}" })
                    {
                        var n = img.Node.FindNodeByPath($"{p}/name")?.GetValueEx<string>(null);
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetItemName({category}/{id}): {ex.Message}"); }
            return string.Empty;
        }

        /// <summary>把数字 id 字符串补零到指定位数（等价于 int 的 D7/D8 格式）</summary>
        private static string PadId(string id, int width) => (id ?? string.Empty).PadLeft(width, '0');

        /// <summary>
        /// 导出精灵图条和配置，支持 Mob/NPC 类型
        /// </summary>
        public (SpriteStrip? strip, PetConfig? config) ExportSpriteStrip(string id, string action, string type = "Mob")
        {
            var (_, strip, config) = BuildSpriteStrip(id, action, type);
            return (strip, config);
        }

        /// <summary>
        /// 从 WZ 提取精灵图条 PNG（拼接所有帧为水平条带），支持 Mob/NPC
        /// </summary>
        public byte[]? BuildSpriteStripPng(string id, string action, string type = "Mob")
        {
            var (pngData, _, _) = BuildSpriteStrip(id, action, type);
            return pngData;
        }

        /// <summary>实体精灵 img 节点：信息全在 {type}/{id}.img（动作/origin/delay/PNG-outlink）；_Canvas 只是 PNG 仓库（经 outlink 跳转，不直接找）。</summary>
        private Wz_Node? FindEntityImg(string type, string id)
        {
            if (!_wzLoaded || _wzs?.WzNode == null || string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id)) return null;
            string pad = PadId(id, 7);
            if (type == "Mob")
            {
                // 文件模式完整 Mob.wz（Mob/{id}.img 有全部信息）；主树目录模式只有 _Canvas 壳（兜底）
                if (_mobFullRoot != null)
                {
                    var n = _mobFullRoot.FindNodeByPath(true, $"Mob/{pad}.img".Split('/'));
                    if (n != null) return n;
                }
                return _wzs.WzNode.FindNodeByPath(true, $"Mob/{pad}.img".Split('/'))
                    ?? _wzs.WzNode.FindNodeByPath(true, $"Mob/_Canvas/{pad}.img".Split('/'));
            }
            return _wzs.WzNode.FindNodeByPath(true, $"{type}/{pad}.img".Split('/'));
        }

        /// <summary>
        /// 单次遍历同时产出 PNG 条带 + strip 元数据 + config（合并原 ExportSpriteStrip / BuildSpriteStripPng 的重复遍历）。
        /// 支持 Mob/NPC 类型。帧来源：{type}/{id}.img 的 action 节点（origin/delay/a0/a1 在 child，PNG 经 outlink 取）。
        /// </summary>
        public (byte[]? pngData, SpriteStrip? strip, PetConfig? config) BuildSpriteStrip(string id, string action, string type = "Mob")
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return (null, null, null);
            lock (_wzLock)
            try
            {
                // Meta: {type}/{id}.img — origin 在 child 上，PNG 通过 child outlink 取
                var metaImgNode = FindEntityImg(type, id);
                if (metaImgNode == null) return (null, null, null);
                var metaImg = metaImgNode.GetValue<Wz_Image>();
                if (metaImg == null || !metaImg.TryExtract()) return (null, null, null);
                var actionNode = metaImg.Node.Nodes.FirstOrDefault(n => n.Text == action);
                if (actionNode == null) return (null, null, null);

                // 单次遍历：收集帧像素 + 元数据
                var frames = new List<(byte[] bgra, int w, int h, int ox, int oy)>();
                var frameDataList = new List<FrameData>();
                int f = 0;
                int maxL = 0, maxT = 0, maxR = 0, maxB = 0;
                foreach (Wz_Node child in actionNode.Nodes)
                {
                    var origin = child.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
                    int delay = child.FindNodeByPath("delay")?.GetValueEx<int>(120) ?? 120;
                    int a0 = child.FindNodeByPath("a0")?.GetValueEx<int>(255) ?? 255;
                    int a1 = child.FindNodeByPath("a1")?.GetValueEx<int>(255) ?? 255;

                    var pngNode = child.ResolveUol() ?? child;
                    var linkNode = GetLinkedSourceNode(pngNode, _mobFullRoot) ?? pngNode;
                    var png = linkNode.GetValue<Wz_Png>() ?? pngNode.GetValue<Wz_Png>();
                    // 跳过无效帧：null 或 1×1 退化占位（WZ 里部分空动作帧无有效画面），避免合成出空白条带
                    if (png == null || png.Width <= 1 || png.Height <= 1) continue;
                    int ox = origin?.X ?? 0, oy = origin?.Y ?? 0;
                    int fw = png.Width, fh = png.Height;

                    frames.Add((PngEncoder.DecodePixels(png), fw, fh, ox, oy));
                    maxL = Math.Max(maxL, ox);
                    maxT = Math.Max(maxT, oy);
                    maxR = Math.Max(maxR, fw - ox);
                    maxB = Math.Max(maxB, fh - oy);
                    frameDataList.Add(new FrameData { Index = f++, Delay = delay, A0 = a0, A1 = a1 });
                }
                if (frames.Count == 0 || frameDataList.Count == 0) return (null, null, null);

                int cw = maxL + maxR, ch = maxT + maxB;
                int finalOx = maxL, finalOy = maxT;

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
                var pngData = PngEncoder.Encode(stripBgra, cw * frames.Count, ch);

                var strip = new SpriteStrip
                {
                    Action = action,
                    FrameCount = frameDataList.Count,
                    FrameWidth = cw,
                    FrameHeight = ch,
                    OriginX = finalOx,
                    OriginY = finalOy,
                    FrameData = frameDataList,
                    DefaultDelay = 120
                };

                var config = new PetConfig
                {
                    Name = $"{type.ToLower()}_{id}",
                    Type = type.ToLower(),
                    OriginX = 0,
                    OriginY = 0,
                    Sprites = new Dictionary<string, SpriteStrip> { [action] = strip }
                };

                return (pngData, strip, config);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] BuildSpriteStrip({id}/{action}/{type}): {ex.Message}");
                return (null, null, null);
            }
        }

        // 保留旧签名兼容
        public (SpriteStrip? strip, PetConfig? config) ExportSpriteStrip(string mobId, string action) => ExportSpriteStrip(mobId, action, "Mob");

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

                var imgNode = FindEntityImg(type, id);
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

                var node = FindNodeByPath($"Mob/{PadId(mobId, 7)}.img/{action}");
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

        // 信息源路径（origin/delay/动作结构全在 {type}/{id}.img；_Canvas 只是 PNG 仓库，经 outlink 跳转）
        public string GetMobWzPath(string mobId, string action, int frame) =>
            $"Mob/{PadId(mobId, 7)}.img/{action}/{frame}";

        public string GetNpcWzPath(string npcId, string action, int frame) =>
            $"Npc/{PadId(npcId, 7)}.img/{action}/{frame}";

        public string GetBalloonTilePath(string balloonId, string tileName) =>
            $"UI/ChatBalloon.img/{balloonId}/{tileName}";

        /// <summary>
        /// 枚举全部**有效**气泡（胶水规则 2026-08-08：气泡是戒指装备的附属属性）：
        /// 遍历 Character/Ring/{code}.img 的 info/chatBalloon 字段 → 气泡 id；
        /// 名字取 String/Eqp.img/Eqp/Ring/{code去前导零}/name 的中文名（气泡名 = 关联装备名）。
        /// 探针实证：Character/Ring 1794 个戒指，01112200.img → chatBalloon=1；String 1112001 → 恋人戒指。
        /// D9：结果缓存（首次构建后不再每次锁内 TryExtract 全部戒指），WZ 重载清空；返回浅拷贝防调用方污染缓存。
        /// </summary>
        public List<(int Id, string Name)> GetBalloonList()
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return new List<(int Id, string Name)>();
            lock (_cacheLock)
            {
                if (_balloonListCache != null)
                {
                    return new List<(int Id, string Name)>(_balloonListCache);
                }
            }
            lock (_wzLock)
            {
                // 双检：并发首次构建只建一次（_wzLock 串行化 WZ 遍历；锁序 _wzLock → _cacheLock，无逆序）
                lock (_cacheLock)
                {
                    if (_balloonListCache != null)
                    {
                        return new List<(int Id, string Name)>(_balloonListCache);
                    }
                }
                var result = new List<(int Id, string Name)>();
                try
                {
                    var ringDir = _wzs.WzNode.FindNodeByPath("Character", true)?.Nodes.FirstOrDefault(n => n.Text == "Ring");
                    if (ringDir != null)
                    {
                        var seen = new HashSet<int>();
                        foreach (Wz_Node n in ringDir.Nodes)
                        {
                            if (!n.Text.EndsWith(".img")) continue;
                            try
                            {
                                var img = n.GetValue<Wz_Image>();
                                if (img == null || !img.TryExtract()) continue;
                                var cb = img.Node.FindNodeByPath("info")?.FindNodeByPath("chatBalloon")?.GetValueEx<int>(0);
                                if (cb == null || cb.Value <= 0) continue;
                                if (!seen.Add(cb.Value)) continue; // 多戒指共用同一气泡 → 取第一个名字
                                var strCode = n.Text[..^4].TrimStart('0');
                                var name = strCode.Length > 0 ? GetRingName(strCode) : null;
                                result.Add((cb.Value, string.IsNullOrEmpty(name) ? $"气泡 {cb.Value}" : name));
                            }
                            catch { /* 单个戒指解析失败跳过 */ }
                        }
                        result.Sort((a, b) => a.Id.CompareTo(b.Id));
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[WzService] GetBalloonList: {ex.Message}"); }
                lock (_cacheLock)
                {
                    _balloonListCache = result;
                }
                return new List<(int Id, string Name)>(result);
            }
        }

        /// <summary>取戒指中文名：String/Eqp.img/Eqp/Ring/{code}/name（code 为去前导零的 7 位短码）。</summary>
        private string? GetRingName(string strCode)
        {
            try
            {
                var imgNode = FindNodeByPath("String/Eqp.img");
                var img = imgNode?.GetValue<Wz_Image>();
                if (img == null || !img.TryExtract()) return null;
                var node = img.Node.FindNodeByPath("Eqp")?.FindNodeByPath("Ring")?.FindNodeByPath(strCode);
                return node?.FindNodeByPath("name")?.GetValueEx<string>(null);
            }
            catch { return null; }
        }

        /// <summary>
        /// 取 ChatBalloon.img/{id} 节点（提取后），用于读 9-slice 切片和 clr。
        /// 新版客户端 UI.wz 是空壳（79B），真实素材在 UI/_Canvas.wz → 优先 _Canvas 路径，回退旧路径。
        /// </summary>
        public Wz_Node? GetBalloonIdNode(string id)
        {
            if (!_wzLoaded || _wzs?.WzNode == null) return null;
            lock (_wzLock)
            try
            {
                foreach (var imgPath in new[] { "UI/_Canvas/ChatBalloon.img", "UI/ChatBalloon.img" })
                {
                    var imgNode = _wzs.WzNode.FindNodeByPath(true, imgPath.Split('/'));
                    var img = imgNode?.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract()) continue;
                    var node = img.Node.FindNodeByPath(id);
                    if (node != null) return node;
                }
                return null;
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
                    NW = ExtractTilePng(node, "nw"), NWOrigin = ExtractTileOrigin(id, node, "nw"),
                    N = ExtractTilePng(node, "n"), NOrigin = ExtractTileOrigin(id, node, "n"),
                    NE = ExtractTilePng(node, "ne"), NEOrigin = ExtractTileOrigin(id, node, "ne"),
                    W = ExtractTilePng(node, "w"), WOrigin = ExtractTileOrigin(id, node, "w"),
                    C = ExtractTilePng(node, "c"), COrigin = ExtractTileOrigin(id, node, "c"),
                    E = ExtractTilePng(node, "e"), EOrigin = ExtractTileOrigin(id, node, "e"),
                    SW = ExtractTilePng(node, "sw"), SWOrigin = ExtractTileOrigin(id, node, "sw"),
                    S = ExtractTilePng(node, "s"), SOrigin = ExtractTileOrigin(id, node, "s"),
                    SE = ExtractTilePng(node, "se"), SEOrigin = ExtractTileOrigin(id, node, "se"),
                    Arrow = ExtractTilePng(node, "arrow"), ArrowOrigin = ExtractTileOrigin(id, node, "arrow"),
                    Head = ExtractTilePng(node, "head"), HeadOrigin = ExtractTileOrigin(id, node, "head")
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
                if (n == null) return Array.Empty<byte>();
                // ChatBalloon 旧路径切片节点全是 outlink（direct 拿到 1×1 退化占位），
                // 必须解析链接链取真实 PNG（参考 Java WzCanvasProperty.pngBytes 递归）。
                var pngNode = n.ResolveUol() ?? n;
                var linkNode = GetLinkedSourceNode(pngNode) ?? pngNode;
                var png = linkNode.GetValue<Wz_Png>() ?? pngNode.GetValue<Wz_Png>();
                if (png == null || png.Width <= 1 || png.Height <= 1) return Array.Empty<byte>();
                return PngEncoder.Encode(PngEncoder.DecodePixels(png), png.Width, png.Height);
            }
            catch { return Array.Empty<byte>(); }
        }

        /// <summary>
        /// 取切片节点 origin。新版 UI/_Canvas 切片节点没有 origin（origin 在旧路径源节点上），
        /// 先查 _Canvas 节点自身，没有再回退旧路径 `UI/ChatBalloon.img/{id}/{tile}` 取源节点 origin
        /// （参考 MapleSalon2 ChatBalloonPiece：origin = info.origin，从源节点取）。
        /// </summary>
        private (int X, int Y) ExtractTileOrigin(string balloonId, Wz_Node parent, string name)
        {
            try
            {
                var n = parent.FindNodeByPath(name);
                var origin = n?.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
                if (origin != null) return (origin.X, origin.Y);
                // 回退旧路径源节点（_Canvas 切片无 origin）
                var src = FindNodeByPath(GetBalloonTilePath(balloonId, name));
                origin = src?.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
                return origin != null ? (origin.X, origin.Y) : (0, 0);
            }
            catch { return (0, 0); }
        }

        public List<(string Id, string Name)> GetDirectoryChildren(string wzDirectory)
        {
            var result = new List<(string Id, string Name)>();
            try
            {
                // 2026-08-16：改直接遍历目录子节点——原只读 {dir}/_Canvas 内部容器，
                // 实测大量 img 不在其中（Hair 15886/17337、Face 9918/12256、Weapon 4070/7506 → 素材列表全部缺件）。
                // 目录 Nodes 为 lazyload 全量子节点（含 _Canvas 等内部节点，按 .img 后缀过滤）。
                var node = FindNodeByPath(wzDirectory);
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
        /// 枚举 img 文件内部数字 id 子节点（2026-08-16 椅子专用）：
        /// GetDirectoryChildren 只适用于「目录」（子节点是 .img 文件，按 .img 后缀过滤）；
        /// 而 Item/Install/03010.img 这类 img 文件是 Wz_Image 节点，其子节点是 03010000 等数字 id——
        /// 必须先 GetValue&lt;Wz_Image&gt; + TryExtract 后遍历 img.Node 才能拿到（未提取时 Nodes 为空 → 椅子列表空根因）。
        /// 返回无前导零 id（匹配 String.wz 键）。
        /// </summary>
        public List<string> GetImgChildren(string imgPath)
        {
            var result = new List<string>();
            try
            {
                lock (_wzLock)
                {
                    var node = _wzs?.WzNode.FindNodeByPath(true, imgPath.Replace('/', '\\').Split('\\'));
                    if (node == null) return result;
                    var img = node.GetValue<Wz_Image>();
                    if (img == null || !img.TryExtract()) return result;
                    foreach (Wz_Node child in img.Node.Nodes)
                    {
                        if (int.TryParse(child.Text, out var idNum))
                        {
                            result.Add(idNum.ToString()); // 无前导零，匹配 String.wz 键
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WzService] GetImgChildren({imgPath}): {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 枚举 WZ 全量地图 id（转发 MapCatalogService）。持 _wzLock。
        /// </summary>
        public List<string> ListMapIds() => _mapCatalog.ListMapIds();

        /// <summary>
        /// 构建 id→显示名 字典（转发 MapCatalogService）。持 _wzLock。
        /// </summary>
        public Dictionary<string, string> BuildMapNameCache() => _mapCatalog.BuildMapNameCache();

        /// <summary>在 _wzLock 内执行操作（可重入）：供 MapService.LoadMap 等外部消费者把整段节点遍历串行化。</summary>
        public T WithWzLock<T>(Func<T> action)
        {
            lock (_wzLock) return action();
        }

        /// <summary>等待地图目录缓存就绪（转发 MapCatalogService）。供 MapBrowserWindow 后台消费。</summary>
        public bool WaitForMapCatalog(int timeoutMs = 10000) => _mapCatalog.WaitForMapCatalog(timeoutMs);

        /// <summary>取地图显示名（转发 MapCatalogService，未命中回退 map_{id}）。</summary>
        public string GetMapName(string mapId) => _mapCatalog.GetMapName(mapId);

        /// <summary>
        /// 后台构建地图目录缓存（转发 MapCatalogService，单飞 + 版本化）：LoadWz 成功后触发。
        /// </summary>
        public void EnsureMapCatalog() => _mapCatalog.EnsureMapCatalog();

        /// <summary>
        /// 构建 String/Map.img 的「区域 → 街道 → 地图」三级层级（转发 MapCatalogService，F6 级联筛选）。持 _wzLock。
        /// </summary>
        public List<MapRegionNode> BuildMapHierarchy() => _mapCatalog.BuildMapHierarchy();

        /// <summary>
        /// 取区域→街道→地图 三级层级（转发 MapCatalogService）：目录已就绪则用缓存，否则现场构建并缓存。
        /// 供背景设置 tab 的级联下拉消费。
        /// </summary>
        public List<MapRegionNode> GetMapHierarchy() => _mapCatalog.GetMapHierarchy();

        /// <summary>世界地图树（树状选择器；Java familyMap/mapListCode 逻辑，预渲染本地缓存）。</summary>
        public List<WorldMapTreeNode> GetWorldMapTree() => _mapCatalog.GetWorldMapTree();

        /// <summary>mapId → "街道|地图名"。</summary>
        public Dictionary<string, string> GetMapStreetInfo() => _mapCatalog.GetMapStreetInfo();

        /// <summary>自动检测素材类型（Npc/Mob）：Npc.wz 有 → Npc，否则 Mob（_Canvas）有 → Mob，都没有 → 空。</summary>
        public string DetectEntityType(string id)
        {
            if (!_wzLoaded || _wzs?.WzNode == null || string.IsNullOrEmpty(id)) return "";
            lock (_wzLock)
            {
                try
                {
                    string pad = PadId(id, 7);
                    if (_wzs.WzNode.FindNodeByPath(true, $"Npc/{pad}.img".Split('/')) != null) return "Npc";
                    if (_wzs.WzNode.FindNodeByPath(true, $"Mob/_Canvas/{pad}.img".Split('/')) != null) return "Mob";
                    if (_wzs.WzNode.FindNodeByPath(true, $"Mob/{pad}.img".Split('/')) != null) return "Mob";
                }
                catch { }
            }
            return "";
        }

        /// <summary>懒加载最底层区域的叶子地图（MapList + portal 传送门关联，点击展开时调用）。</summary>
        public List<WorldMapTreeNode> LoadLeafMaps(WorldMapTreeNode node) => _mapCatalog.LoadLeafMaps(node);

        /// <summary>区域节点自身地图叶子（仅 MapList；有子区域的区域展开时调用）。</summary>
        public List<WorldMapTreeNode> LoadSelfMaps(WorldMapTreeNode node) => _mapCatalog.LoadSelfMaps(node);

        /// <summary>启动后注入缓存（世界地图树预渲染缓存用）。</summary>
        public void SetCacheManager(CacheManager cache) => _mapCatalog.SetCache(cache);

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

        private static Wz_Node? GetLinkedSourceNode(Wz_Node node, Wz_Node? mobFullFallback = null)
        {
            try
            {
                // 循环解析链接链：source/_inlink/_outlink 的目标本身可能还是 UOL/链接，
                // 一路解到真实 canvas（参考 Java WzCanvasProperty.pngBytes 递归 + WzSpriteHandler 的 Map2 兜底）。
                // 限深 8 防环（WZ 链接不会形成环，但防御性上限避免异常数据死循环）。
                for (int depth = 0; depth < 8; depth++)
                {
                    node = node.ResolveUol() ?? node;

                    var sourceAc = node.Nodes["source"].GetValueEx<string>(null);
                    if (!string.IsNullOrEmpty(sourceAc))
                    {
                        var wzFile = node.GetNodeWzFile();
                        var target = wzFile?.WzStructure?.WzNode.FindNodeByPath(true, sourceAc.Split('/'));
                        if (target != null) { node = target; continue; }
                    }

                    var inlink = node.Nodes["_inlink"].GetValueEx<string>(null);
                    if (!string.IsNullOrEmpty(inlink))
                    {
                        var img = node.GetNodeWzImage();
                        var target = img?.Node.FindNodeByPath(true, inlink.Split('/'));
                        if (target != null) { node = target; continue; }
                    }

                    var outlink = node.Nodes["_outlink"].GetValueEx<string>(null);
                    if (!string.IsNullOrEmpty(outlink))
                    {
                        var wzFile = node.GetNodeWzFile();
                        var target = wzFile?.WzStructure?.WzNode.FindNodeByPath(true, outlink.Split('/'));
                        if (target == null && outlink.StartsWith("Map/"))
                        {
                            // Java WzSpriteHandler 兜底：outlink 指向 Map/ 但实际在 Map2/（老地图资源目录）
                            target = wzFile?.WzStructure?.WzNode.FindNodeByPath(true, ("Map2/" + outlink.Substring(4)).Split('/'));
                        }
                        if (target == null && mobFullFallback != null && outlink.StartsWith("Mob/"))
                        {
                            // 文件模式 Mob.wz：outlink 指向 Mob/_Canvas/...（PNG 仓库），在完整树里取
                            target = mobFullFallback.FindNodeByPath(true, outlink.Split('/'));
                        }
                        if (target != null) { node = target; continue; }
                    }

                    return node;
                }
                return node;
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

    /// <summary>
    /// 世界地图树节点（Map/WorldMap 层级：顶级区域 → 子区域 → …，参考 Java familyMap/mapListCode 逻辑）。
    /// 供背景设置 tab 的树状选择器；MapIds = 本节点 MapList 地图代码；TotalCount = 本节点+子孙地图总数。
    /// 最底层区域（无子区域）挂一个 LeafPlaceholder 占位子节点，展开时懒加载叶子地图
    /// （MapList 地图 + portal 传送门关联地图；胶水规则 2026-08-08，点击才渲染避免低性能）。
    /// </summary>
    public sealed class WorldMapTreeNode
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> MapIds { get; set; } = new();
        public int TotalCount { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<WorldMapTreeNode> Children { get; set; } = new();
        // 懒加载叶子标记
        public bool IsLeafPlaceholder { get; set; }   // 占位（未加载叶子，点击展开时加载）
        public bool LeafLoaded { get; set; }          // 叶子已加载
        public bool IsLeafMap { get; set; }           // 叶子地图节点
        public string LeafMapId { get; set; } = string.Empty;  // 叶子地图 id
        public bool IsTown { get; set; }              // 城镇地图（Map.wz info/town==1）——展开分组：子目录→城镇→非城镇
    }
}
