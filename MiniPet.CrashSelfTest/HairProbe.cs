using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 发型渲染调研探针（StartupObject 模式，参考 CapeProbe 写法）：
/// ① 全量扫描 Character/Hair 目录：每个发型 img 的结构分类（stand1/0 帧结构 / default 结构 / 两者 / 都无）；
/// ② 每个发型跑 PaperdollService.ProbeLayerResolution 完整管线（stand1/0），统计「发型件是否被收集/落层」；
/// ③ 对用户场景常见 id + 各系列代表 id 打印详细结构（帧子节点、hair 节点类型/出图、管线落层、CompositeFrame 出图）。
/// 修复前后各跑一次 → 对比找出「该显示不显示」的发型模式与修复效果。
/// 用法：dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.HairProbe
/// </summary>
public static class HairProbe
{
    // 用户场景常见/代表 id（30000/31000/32000/33000 系列各若干）
    private static readonly string[] TestIds =
    {
        "30000", "30001", "30002", "30003", "30004", "30005", "30006",
        "31000", "31001", "31002", "31100", "31101",
        "32000", "32001", "32002", "32100",
        "33000", "33001", "33002", "33100",
        "34000", "34001", "35000",
    };

    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
        var cts = new CancellationTokenSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Run();
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"probe error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                exitCode = 1;
            }
            // 取消主循环后正常退出（避免 Environment.Exit 在 Avalonia Headless 下挂死、进程残留占用 WZ 文件）
            cts.Cancel();
        });
        Dispatcher.UIThread.MainLoop(cts.Token);
        return exitCode;
    }

    private static void Run()
    {
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        // 环境变量 MINIPET_WZ_BASE 可覆盖 WZ 路径（桌宠运行中锁文件时，探针可指向副本）
        string basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        if (string.IsNullOrEmpty(basePath)) basePath = wzConfig.BaseWzPath;
        Console.WriteLine($"WZ 路径: {basePath}");
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) return;

        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));

        var hairDir = wz.FindNodeByPath("Character/Hair");
        if (hairDir == null)
        {
            Console.WriteLine("Character/Hair 目录不存在！");
            return;
        }
        var imgIds = new List<string>();
        foreach (Wz_Node c in hairDir.Nodes)
        {
            if (!c.Text.EndsWith(".img")) continue;
            var id = c.Text[..^4];
            if (id.Length >= 7 && id.StartsWith("0")) id = id.TrimStart('0');
            if (int.TryParse(id, out _)) imgIds.Add(id);
        }
        Console.WriteLine($"\n========== 发型目录扫描 ==========");
        Console.WriteLine($"发型 img 总数: {imgIds.Count}");
        Console.WriteLine($"前 30 个 id: {string.Join(", ", imgIds.Take(30))}");
        Console.WriteLine($"末 10 个 id: {string.Join(", ", imgIds.Skip(Math.Max(0, imgIds.Count - 10)))}");

        ScanAllHair(wz, paperdoll, imgIds);
        TestCapHidesHair(wz, paperdoll);
        DumpSmapHair(wz);
        DumpHairShadeDetail(wz, "30000");
        SaveSamplePngs(wz, "30000");
        DumpTestIds(wz, paperdoll, imgIds);
    }

    /// <summary>验证「戴帽子是否把头发整层隐藏」（遮挡锁 vslot 并集近似 vs smap 真实 requireLocks）。</summary>
    private static void TestCapHidesHair(WzService wz, PaperdollService paperdoll)
    {
        Console.WriteLine("\n========== 帽子遮挡锁测试（cap + hair）==========");
        foreach (var capId in new[] { "1000000", "1002149", "1003000" })
        {
            string capRoot = $"Character/Cap/{capId.PadLeft(8, '0')}.img";
            if (wz.FindNodeByPath(capRoot) == null)
            {
                Console.WriteLine($"  [{capId}] Cap img 不存在，跳过");
                continue;
            }
            var appearance = new CharacterAppearance
            {
                Gender = 0,
                BodyId = 2000,
                Ear = PaperdollService.EarHuman,
                Hair = new ItemInfo { Id = "30000" },
                Cap = new ItemInfo { Id = capId },
            };
            var items = paperdoll.ProbeLayerResolution(appearance, "stand1", 0);
            var hairItems = items.Where(x => x.Category == "hair").ToList();
            Console.WriteLine($"  cap={capId}: hair 件 {hairItems.Count} 个, 可见(hidden=false) {hairItems.Count(x => !x.Hidden)} 个");
            foreach (var it in hairItems)
            {
                Console.WriteLine($"    piece={it.PieceName,-16} 层={it.ResolvedLayer,-16} locked={it.Locked,5} hidden={it.Hidden,5}");
            }
            // 对照：无 cap
            var noCap = new CharacterAppearance { Gender = 0, BodyId = 2000, Ear = PaperdollService.EarHuman, Hair = new ItemInfo { Id = "30000" } };
            var itemsNoCap = paperdoll.ProbeLayerResolution(noCap, "stand1", 0);
            Console.WriteLine($"  对照无 cap: hair 可见 {itemsNoCap.Count(x => x.Category == "hair" && !x.Hidden)} 个");
        }
    }

    /// <summary>dump smap.img 中 hair 相关层的真实 requireLocks（MapleSalon2 用 smap 而非 vslot 并集）。</summary>
    private static void DumpSmapHair(WzService wz)
    {
        Console.WriteLine("\n========== smap.img hair 相关层 ==========");
        var smap = wz.FindNodeByPath("smap.img");
        if (smap == null)
        {
            Console.WriteLine("smap.img 不存在");
            return;
        }
        foreach (Wz_Node c in smap.Nodes)
        {
            if (!c.Text.Contains("hair", StringComparison.OrdinalIgnoreCase) &&
                !c.Text.Equals("Hr", StringComparison.Ordinal) &&
                !c.Text.Equals("Cp", StringComparison.Ordinal)) continue;
            var val = c.GetValueEx<string>(null);
            Console.WriteLine($"  {c.Text} = \"{val}\"");
        }
        // 全部层数统计
        int count = 0;
        foreach (Wz_Node c in smap.Nodes) count++;
        Console.WriteLine($"  smap.img 总层数: {count}");
    }

    /// <summary>导出 30000 的 hair/hairOverHead/hairShade/0 PNG 到 /tmp/hairprobe-pngs/ 供人工查看。</summary>
    private static void SaveSamplePngs(WzService wz, string id)
    {
        string root = $"Character/Hair/{id.PadLeft(8, '0')}.img";
        var dir = "/tmp/hairprobe-pngs";
        Directory.CreateDirectory(dir);
        foreach (var (name, path) in new[]
        {
            ("hair", $"{root}/default/hair"),
            ("hairOverHead", $"{root}/default/hairOverHead"),
            ("hairShade0", $"{root}/default/hairShade/0"),
            ("hairShade1", $"{root}/default/hairShade/1"),
        })
        {
            var png = wz.ExtractPng(path);
            if (png != null)
            {
                File.WriteAllBytes(Path.Combine(dir, $"{id}-{name}.png"), png);
                Console.WriteLine($"[导出] {id}-{name}.png {png.Length} 字节 → {dir}");
            }
            else
            {
                Console.WriteLine($"[导出] {id}-{name}.png 无图");
            }
        }
        // 全量 dump default/hairShade 子节点
        var shade = wz.FindNodeByPath($"{root}/default/hairShade");
        if (shade != null)
        {
            Console.WriteLine($"default/hairShade 全部子节点:");
            foreach (Wz_Node c in shade.Nodes)
            {
                var uol = c.GetValueEx<Wz_Uol>(null);
                var png = wz.ExtractPng($"{root}/default/hairShade/{c.Text}");
                var cPath = $"{root}/default/hairShade/{c.Text}";
                Console.WriteLine($"  {c.Text}: value={c.Value?.GetType().Name} uol={(uol != null ? $"\"{uol.Uol}\"" : "无")} 出图={(png != null ? png.Length + "字节" : "✗")} origin={wz.FindNodeByPath($"{cPath}/origin") != null} map={wz.FindNodeByPath($"{cPath}/map") != null} z={wz.FindNodeByPath($"{cPath}/z") != null}");
            }
        }
        // hairShade/0 的 origin/map/z 数值 + 上层 default/hair 对照
        foreach (var p in new[] { "default/hair", "default/hairOverHead", "default/hairShade/0" })
        {
            var origin = wz.GetOrigin($"{root}/{p}");
            var z = wz.GetZField($"{root}/{p}");
            Console.WriteLine($"[{p}] origin=({origin.x},{origin.y}) z={z}");
        }
        // 无 hair 子节点的 12 个发型：检查 backHair/backDefault/其他候选
        foreach (var bid in new[] { "35400", "45751", "61720" })
        {
            string broot = $"Character/Hair/{bid.PadLeft(8, '0')}.img";
            Console.WriteLine($"\n== 无 hair 子节点发型 [{bid}] 候选检查 ==");
            foreach (var cand in new[] { "backHair", "backDefault/hair", "backDefault/hairOverHead", "default/backHair", "default/hair", "default/hairSahde" })
            {
                var n = wz.FindNodeByPath($"{broot}/{cand}");
                if (n != null)
                {
                    var png = wz.ExtractPng($"{broot}/{cand}");
                    Console.WriteLine($"  {cand}: 存在 value={n.Value?.GetType().Name} 出图={(png != null ? png.Length + "字节" : "✗")}");
                }
            }
        }
    }

    /// <summary>诊断 hairShade 部件的 UOL 结构与出图情况（对照正常发型）。</summary>
    private static void DumpHairShadeDetail(WzService wz, string id)
    {
        string root = $"Character/Hair/{id.PadLeft(8, '0')}.img";
        Console.WriteLine($"\n========== hairShade 结构诊断 [{id}] ==========");
        var frame = wz.FindNodeByPath($"{root}/stand1/0");
        if (frame != null)
        {
            foreach (Wz_Node c in frame.Nodes)
            {
                if (!c.Text.Contains("hair", StringComparison.OrdinalIgnoreCase)) continue;
                var uol = c.GetValueEx<Wz_Uol>(null);
                Console.WriteLine($"  stand1/0/{c.Text}: value={c.Value?.GetType().Name} uol={(uol != null ? $"\"{uol.Uol}\"" : "无")}");
                var target = c.ResolveUol();
                Console.WriteLine($"    ResolveUol → {(target == null ? "null" : target.FullPathToFile)} value={target?.Value?.GetType().Name}");
                var png = wz.ExtractPng($"{root}/stand1/0/{c.Text}");
                Console.WriteLine($"    ExtractPng → {(png != null ? png.Length + " 字节" : "✗无")}");
            }
        }
        var dflt = wz.FindNodeByPath($"{root}/default");
        if (dflt != null)
        {
            foreach (Wz_Node c in dflt.Nodes)
            {
                var uol = c.GetValueEx<Wz_Uol>(null);
                var kids = new List<string>();
                foreach (Wz_Node k in c.Nodes) kids.Add(k.Text);
                Console.WriteLine($"  default/{c.Text}: value={c.Value?.GetType().Name} uol={(uol != null ? $"\"{uol.Uol}\"" : "无")} 子节点=[{string.Join(",", kids.Take(8))}]");
                var png = wz.ExtractPng($"{root}/default/{c.Text}");
                Console.WriteLine($"    ExtractPng → {(png != null ? png.Length + " 字节" : "✗无")}");
                // hairShade 是嵌套数字结构：default/hairShade/{0,1,2...} 内是 canvas
                if (c.Text.Contains("Shade", StringComparison.OrdinalIgnoreCase) && kids.Count > 0)
                {
                    foreach (var k in kids.Take(3))
                    {
                        var sub = wz.FindNodeByPath($"{root}/default/{c.Text}/{k}");
                        if (sub == null) continue;
                        var subPng = wz.ExtractPng($"{root}/default/{c.Text}/{k}");
                        var subUol = sub.GetValueEx<Wz_Uol>(null);
                        Console.WriteLine($"    default/{c.Text}/{k}: value={sub.Value?.GetType().Name} uol={(subUol != null ? $"\"{subUol.Uol}\"" : "无")} 出图={(subPng != null ? subPng.Length + "字节" : "✗无")}");
                    }
                }
            }
        }
    }

    // ── ① 全量结构分类 + 管线收集统计 ──
    private static void ScanAllHair(WzService wz, PaperdollService paperdoll, List<string> imgIds)
    {
        Console.WriteLine($"\n========== 全量发型结构分类 + 管线收集（stand1/0）==========");
        int hasActionFrame = 0, hasDefault = 0, both = 0, neither = 0;
        int collected = 0, notCollected = 0;
        var notCollectedIds = new List<string>();
        var notCollectedWithDefault = new List<string>();
        var notCollectedNoDefault = new List<string>();
        int actionHairPng = 0, defaultHairPng = 0;
        var hairPngFailIds = new List<string>();      // stand1/0/hair 节点存在但 ExtractPng 失败
        var noHairChildIds = new List<string>();      // stand1/0 存在但无 hair 子节点
        var piecePngFail = new Dictionary<string, int>(); // 各 hair 部件出图失败计数（按部件名）
        int shadeTotal = 0, shadeVisible = 0;
        var shadeStillBroken = new List<string>();    // hairShade 件存在但未锁定可见（修复后仍坏）

        foreach (var id in imgIds)
        {
            string root = $"Character/Hair/{id.PadLeft(8, '0')}.img";
            try
            {
                bool hasFrame = wz.FindNodeByPath($"{root}/stand1/0") != null;
                bool hasDflt = wz.FindNodeByPath($"{root}/default") != null;
                bool hasActionHair = wz.FindNodeByPath($"{root}/stand1/0/hair") != null;
                bool hasDefaultHair = wz.FindNodeByPath($"{root}/default/hair") != null;

                if (hasFrame && hasDflt) both++;
                else if (hasFrame) hasActionFrame++;
                else if (hasDflt) hasDefault++;
                else neither++;

                bool actionHairOk = false;
                if (hasActionHair)
                {
                    var png = wz.ExtractPng($"{root}/stand1/0/hair");
                    if (png != null)
                    {
                        actionHairPng++;
                        actionHairOk = true;
                    }
                    else
                    {
                        hairPngFailIds.Add(id);
                    }
                }
                else
                {
                    // stand1/0 存在但无 hair 子节点 → 该发型的主后发缺失（应显示不显示候选）
                    noHairChildIds.Add(id);
                }
                if (hasDefaultHair && wz.ExtractPng($"{root}/default/hair") != null) defaultHairPng++;

                // 帧内全部 hair 部件出图检查（hair/hairOverHead/hairBelowBody/hairShade/backHair…）
                var frame = wz.FindNodeByPath($"{root}/stand1/0");
                if (frame != null)
                {
                    foreach (Wz_Node c in frame.Nodes)
                    {
                        var name = c.Text;
                        if (!name.Contains("hair", StringComparison.OrdinalIgnoreCase)) continue;
                        if (wz.ExtractPng($"{root}/stand1/0/{name}") == null)
                        {
                            piecePngFail.TryGetValue(name, out var n);
                            piecePngFail[name] = n + 1;
                        }
                    }
                }

                // 管线收集：stand1/0 是否有 hair 件
                var appearance = MakeAppearance(id);
                var items = paperdoll.ProbeLayerResolution(appearance, "stand1", 0);
                bool anyHair = items.Any(x => x.Category == "hair");
                if (anyHair)
                {
                    collected++;
                }
                else
                {
                    notCollected++;
                    notCollectedIds.Add(id);
                    if (hasDflt) notCollectedWithDefault.Add(id);
                    else notCollectedNoDefault.Add(id);
                }
                // 修复验证：hairShade 件是否已锁定可见（canvas 列表解析后应锁定）
                var shadeItems = items.Where(x => x.Category == "hair" && x.PieceName.Equals("hairShade", StringComparison.OrdinalIgnoreCase)).ToList();
                if (shadeItems.Count > 0)
                {
                    shadeTotal++;
                    if (shadeItems.Any(x => x.Locked && !x.Hidden)) shadeVisible++;
                    else shadeStillBroken.Add(id);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{id}] 扫描异常: {ex.Message}");
            }
        }

        Console.WriteLine($"\n结构分类（共 {imgIds.Count}）:");
        Console.WriteLine($"  有 stand1/0 帧结构: {hasActionFrame}");
        Console.WriteLine($"  有 default 结构: {hasDefault}");
        Console.WriteLine($"  两者都有: {both}");
        Console.WriteLine($"  两者都无: {neither}");
        Console.WriteLine($"  stand1/0/hair 出图: {actionHairPng}, default/hair 出图: {defaultHairPng}");
        if (hairPngFailIds.Count > 0)
        {
            Console.WriteLine($"  ⚠ stand1/0/hair 节点存在但出图失败: {hairPngFailIds.Count} 个");
            Console.WriteLine($"    列表: {string.Join(", ", hairPngFailIds.Take(60))}");
        }
        if (piecePngFail.Count > 0)
        {
            Console.WriteLine($"  帧内 hair 部件出图失败分布: {string.Join(", ", piecePngFail.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
        if (noHairChildIds.Count > 0)
        {
            Console.WriteLine($"  ⚠ stand1/0 存在但无 hair 子节点: {noHairChildIds.Count} 个");
            Console.WriteLine($"    列表: {string.Join(", ", noHairChildIds.Take(60))}");
            foreach (var id in noHairChildIds.Take(10))
            {
                DumpHairUol(wz, id);
            }
        }
        Console.WriteLine($"\n管线收集（stand1/0）:");
        Console.WriteLine($"  收集到 hair 件: {collected}");
        Console.WriteLine($"  未收集到 hair 件: {notCollected}");
        Console.WriteLine($"  hairShade 件: 共 {shadeTotal} 个发型, 锁定可见 {shadeVisible}, 仍未锁定 {shadeStillBroken.Count}");
        if (shadeStillBroken.Count > 0)
        {
            Console.WriteLine($"    仍未锁定示例: {string.Join(", ", shadeStillBroken.Take(20))}");
            foreach (var id in shadeStillBroken.Take(4))
            {
                DumpHairUol(wz, id);
            }
        }
        if (notCollectedWithDefault.Count > 0)
        {
            Console.WriteLine($"  其中 img 有 default 结构（stand1/0 无 → 但 default 可能有图）: {notCollectedWithDefault.Count}");
            Console.WriteLine($"    示例: {string.Join(", ", notCollectedWithDefault.Take(30))}");
        }
        if (notCollectedNoDefault.Count > 0)
        {
            Console.WriteLine($"  其中 default 也没有: {notCollectedNoDefault.Count}");
            Console.WriteLine($"    示例: {string.Join(", ", notCollectedNoDefault.Take(30))}");
        }

        // 出图失败 id 详细诊断：UOL 目标 + 解析链
        if (hairPngFailIds.Count > 0)
        {
            Console.WriteLine($"\n========== stand1/0/hair 出图失败诊断（前 20 个）==========");
            foreach (var id in hairPngFailIds.Take(20))
            {
                DumpHairUol(wz, id);
            }
        }
    }

    /// <summary>诊断单个发型 stand1/0 各 hair 部件的 UOL 字符串与解析目标。</summary>
    private static void DumpHairUol(WzService wz, string id)
    {
        string root = $"Character/Hair/{id.PadLeft(8, '0')}.img";
        Console.WriteLine($"\n== [{id}] {root} ==");
        var frame = wz.FindNodeByPath($"{root}/stand1/0");
        if (frame == null)
        {
            Console.WriteLine("  stand1/0 不存在");
            return;
        }
        var frameKids = new List<string>();
        foreach (Wz_Node c in frame.Nodes) frameKids.Add(c.Text);
        Console.WriteLine($"  stand1/0 全部子节点: {string.Join(", ", frameKids)}");
        foreach (Wz_Node c in frame.Nodes)
        {
            if (!c.Text.Contains("hair", StringComparison.OrdinalIgnoreCase)) continue;
            var cPath = $"{root}/stand1/0/{c.Text}";
            string uolVal = c.GetValueEx<Wz_Uol>(null)?.Uol ?? "非UOL";
            var png = wz.ExtractPng(cPath);
            Console.WriteLine($"  {c.Text}: value={c.Value?.GetType().Name} uol=\"{uolVal}\" 出图={(png != null ? $"{png.Length}字节" : "✗无")}");
            // 解析 UOL 目标路径
            try
            {
                var resolved = c.ResolveUol();
                Console.WriteLine($"    UOL 目标: {(resolved == null ? "null" : resolved.FullPathToFile)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    UOL 解析异常: {ex.Message}");
            }
        }
        // default 对应节点
        foreach (var name in new[] { "hair", "hairOverHead", "hairBelowBody", "hairShade", "backHair", "hairSahde" })
        {
            var d = wz.FindNodeByPath($"{root}/default/{name}");
            if (d == null) continue;
            var png = wz.ExtractPng($"{root}/default/{name}");
            Console.WriteLine($"  default/{name}: value={d.Value?.GetType().Name} 出图={(png != null ? $"{png.Length}字节" : "✗无")}");
            // hairShade 容器子节点明细（判断是 canvas 列表还是空节点）
            if (name.Contains("Shade", StringComparison.OrdinalIgnoreCase))
            {
                var kids = new List<string>();
                foreach (Wz_Node k in d.Nodes)
                {
                    var kpng = wz.ExtractPng($"{root}/default/{name}/{k.Text}");
                    kids.Add($"{k.Text}:{k.Value?.GetType().Name ?? "null"}{(kpng != null ? "+png" : "")}");
                }
                Console.WriteLine($"    default/{name} 子节点 [{kids.Count}]: {string.Join(", ", kids.Take(12))}");
            }
        }
    }

    // ── ② 测试 id 详细结构 dump ──
    private static void DumpTestIds(WzService wz, PaperdollService paperdoll, List<string> imgIds)
    {
        Console.WriteLine($"\n========== 测试发型详细结构 ==========");
        foreach (var id in TestIds)
        {
            if (!imgIds.Contains(id))
            {
                Console.WriteLine($"\n== [{id}] Character/Hair/{id.PadLeft(8, '0')}.img 不存在（本 WZ 无此发型）==");
                continue;
            }
            DumpOne(wz, paperdoll, id);
        }

        // 系列代表补扫：每千位系列取第一个（若 TestIds 未覆盖）
        Console.WriteLine($"\n========== 各系列代表（每 1000 段第一个）==========");
        var bySeries = imgIds
            .Select(id => int.Parse(id))
            .GroupBy(n => n / 1000)
            .OrderBy(g => g.Key)
            .ToList();
        foreach (var g in bySeries)
        {
            var first = g.Min().ToString();
            if (!TestIds.Contains(first))
            {
                DumpOne(wz, paperdoll, first);
            }
        }
    }

    private static void DumpOne(WzService wz, PaperdollService paperdoll, string id)
    {
        string root = $"Character/Hair/{id.PadLeft(8, '0')}.img";
        Console.WriteLine($"\n== [{id}] {root} ==");
        var node = wz.FindNodeByPath(root);
        if (node == null)
        {
            Console.WriteLine("  img 不存在");
            return;
        }
        var topKids = new List<string>();
        foreach (Wz_Node c in node.Nodes) topKids.Add($"{c.Text}({Kind(c)})");
        Console.WriteLine($"  顶层子节点: {string.Join(", ", topKids)}");

        // stand1/0 帧结构
        DumpFrame(wz, $"{root}/stand1/0", "stand1/0");
        // default 结构
        DumpFrame(wz, $"{root}/default", "default");

        // 完整管线 stand1/0
        var appearance = MakeAppearance(id);
        var items = paperdoll.ProbeLayerResolution(appearance, "stand1", 0);
        var hairItems = items.Where(x => x.Category == "hair").ToList();
        Console.WriteLine($"  管线 stand1/0 总件数={items.Count}, hair 件={hairItems.Count}");
        foreach (var it in hairItems)
        {
            Console.WriteLine($"    ▶ piece={it.PieceName,-16} z={it.ZField,-14} → 层={it.ResolvedLayer,-16} idx={it.ZIndex,4} map={it.HasMap,5} locked={it.Locked,5} hidden={it.Hidden,5}");
        }
        if (hairItems.Count == 0)
        {
            // 手动验证 default/hair 是否可出图（数据存在但管线没收集 → 该显示不显示）
            var dpng = wz.ExtractPng($"{root}/default/hair");
            Console.WriteLine($"    ✗ 管线未收集 hair 件; default/hair 出图={(dpng != null ? $"{dpng.Length} 字节" : "无")}（有图=缺陷，无图=数据缺失）");
            // 再试 walk1/alert 等其他动作
            foreach (var act in new[] { "walk1", "alert", "jump" })
            {
                var alt = paperdoll.ProbeLayerResolution(appearance, act, 0);
                var altHair = alt.Where(x => x.Category == "hair").Count();
                if (altHair > 0)
                {
                    Console.WriteLine($"    ✓ 但 {act}/0 管线收集到 {altHair} 个 hair 件（动作缺帧，仅 stand1 无）");
                    break;
                }
            }
        }
        // 出图验证（CompositeFrame，stand1/0）
        try
        {
            var png = paperdoll.CompositeFrame(appearance, "stand1", 0);
            Console.WriteLine($"  CompositeFrame(stand1/0) 出图: {(png != null ? $"{png.Length} 字节" : "无")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  CompositeFrame(stand1/0) 异常: {ex.Message}");
        }
    }

    private static void DumpFrame(WzService wz, string framePath, string label)
    {
        var frame = wz.FindNodeByPath(framePath);
        if (frame == null)
        {
            Console.WriteLine($"  {label} → 不存在");
            return;
        }
        var kids = new List<string>();
        foreach (Wz_Node c in frame.Nodes)
        {
            var cPath = $"{framePath}/{c.Text}";
            var uol = c.GetValueEx<Wz_Uol>(null);
            string uolInfo = uol != null ? $"→\"{uol.Uol}\"" : "";
            kids.Add($"{c.Text}({Kind(c)}{uolInfo}{(wz.FindNodeByPath($"{cPath}/origin") != null ? ",o" : "")}{(wz.FindNodeByPath($"{cPath}/map") != null ? ",m" : "")}{(wz.FindNodeByPath($"{cPath}/z") != null ? ",z" : "")})");
        }
        Console.WriteLine($"  {label} 子节点: {string.Join(", ", kids)}");
        // hair 子节点出图
        var hairPng = wz.ExtractPng($"{framePath}/hair");
        if (hairPng != null)
        {
            Console.WriteLine($"    {label}/hair 出图: {hairPng.Length} 字节");
        }
    }

    private static string Kind(Wz_Node c)
    {
        return c.Value switch
        {
            Wz_Png => "png",
            Wz_Uol => "uol",
            Wz_Vector => "vec",
            Wz_Image => "img",
            _ => c.GetValueEx<string>(null) != null ? "str" : "node"
        };
    }

    private static CharacterAppearance MakeAppearance(string hairId)
    {
        return new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Ear = PaperdollService.EarHuman,
            Hair = new ItemInfo { Id = hairId },
        };
    }
}
