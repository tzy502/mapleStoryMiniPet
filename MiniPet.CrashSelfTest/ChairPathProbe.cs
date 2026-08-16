using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>椅子素材验证：0301.img 是否 img 文件（Nodes 需 TryExtract 才有）、
/// GetDirectoryChildren 现状、TryExtract 后椅子 id 数、effect 帧出图。</summary>
public static class ChairPathProbe
{
    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
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
                exitCode = 1;
            }
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode;
    }

    private static void Run()
    {
        var basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath ?? wzConfig.BaseWzPath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) return;

        // 1) 0301.img 是否存在 + 节点类型
        Console.WriteLine("=== 1) Item/Install 目录结构 ===");
        var installDir = wz.FindNodeByPath("Item/Install");
        if (installDir != null)
        {
            var names = new List<string>();
            foreach (var c in installDir.Nodes)
            {
                names.Add(c.Text);
            }
            Console.WriteLine($"  Install 子节点 {names.Count} 个，前 30: {string.Join(",", names.Take(30))}");
        }
        else
        {
            Console.WriteLine("  Item/Install 不存在");
        }

        var imgNode = wz.FindNodeByPath("Item/Install/0301.img");
        Console.WriteLine($"  0301.img 节点: {(imgNode == null ? "不存在（真实分组是 03010.img 等 id/1000 分组）" : $"存在 value={imgNode.Value?.GetType().Name}")}");
        var img = imgNode?.GetValue<Wz_Image>();

        // 2) GetDirectoryChildren 现状（浏览器路径，0301.img 不存在 → 预期 0）
        Console.WriteLine("=== 2) GetDirectoryChildren(Item/Install/0301.img) 现状 ===");
        var listed = wz.GetDirectoryChildren("Item/Install/0301.img");
        Console.WriteLine($"  返回 {listed.Count} 个（0301.img 不存在，浏览器椅子分类空根因）");

        // 2b) 真实分组 img 的 GetDirectoryChildren（img 未 TryExtract 时 Nodes 空？）
        Console.WriteLine("=== 2b) GetDirectoryChildren(Item/Install/03010.img) ===");
        var listed10 = wz.GetDirectoryChildren("Item/Install/03010.img");
        Console.WriteLine($"  返回 {listed10.Count} 个");
        var imgNode10 = wz.FindNodeByPath("Item/Install/03010.img");
        Console.WriteLine($"  03010.img value={imgNode10?.Value?.GetType().Name} TryExtract前 Nodes={imgNode10?.Nodes.Count}");
        var img10 = imgNode10?.GetValue<Wz_Image>();
        var extracted10 = img10 != null && img10.TryExtract();
        Console.WriteLine($"  03010.img TryExtract: {extracted10}, 后 Nodes: {(img10 != null ? img10.Node.Nodes.Count : -1)}");

        // 2c) 修复后加载链路验证：GetImgChildren（LoadFromWz 椅子段新路径）
        Console.WriteLine("=== 2c) 修复后 GetImgChildren(Item/Install/03010.img) ===");
        var fixedList = wz.GetImgChildren("Item/Install/03010.img");
        Console.WriteLine($"  GetImgChildren 返回 {fixedList.Count} 个（预期 664）");
        Console.WriteLine($"  前 10: {string.Join(",", fixedList.Take(10))}");
        Console.WriteLine($"  名字抽查: 3010000=[{wz.GetItemName("Install", "3010000")}] 3010001=[{wz.GetItemName("Install", "3010001")}]");

        // 2d) 完整模拟 LoadFromWz 椅子段：遍历 Install 全部分组 img + 范围过滤
        Console.WriteLine("=== 2d) 模拟 LoadFromWz 椅子段（修复后）===");
        var loadEntries = new List<(string Id, string Name)>();
        var installDir2 = wz.FindNodeByPath("Item/Install");
        if (installDir2 != null)
        {
            foreach (var c in installDir2.Nodes)
            {
                if (!c.Text.EndsWith(".img")) { continue; }
                foreach (var id in wz.GetImgChildren($"Item/Install/{c.Text}"))
                {
                    if (!int.TryParse(id, out var chairId) || chairId < 3010000 || chairId >= 3021000)
                    {
                        continue;
                    }
                    var cn = wz.GetItemName("Install", id);
                    loadEntries.Add((id, string.IsNullOrEmpty(cn) ? $"椅子_{id}" : cn));
                }
            }
        }
        Console.WriteLine($"  椅子条目: {loadEntries.Count} 个（预期 3088）");
        Console.WriteLine($"  前 10: {string.Join(",", loadEntries.Take(10).Select(x => $"{x.Id}={x.Name}"))}");
        Console.WriteLine($"  有名字比例: {loadEntries.Count(x => !x.Name.StartsWith("椅子_"))}/{loadEntries.Count}");
        Console.WriteLine($"  回退名示例: {string.Join(",", loadEntries.Where(x => x.Name.StartsWith("椅子_")).Take(5).Select(x => x.Id))}");

        // 3) TryExtract 后直接遍历数字 id 子节点（03010.img）
        Console.WriteLine("=== 3) TryExtract 后遍历 id 子节点（03010.img）===");
        var chairIds = new List<string>();
        if (img10 != null && extracted10)
        {
            foreach (Wz_Node child in img10.Node.Nodes)
            {
                if (int.TryParse(child.Text, out _))
                {
                    chairIds.Add(child.Text);
                }
            }
        }
        Console.WriteLine($"  数字 id 椅子: {chairIds.Count} 个");
        Console.WriteLine($"  前 20: {string.Join(",", chairIds.Take(20))}");
        Console.WriteLine($"  末 10: {string.Join(",", chairIds.Skip(Math.Max(0, chairIds.Count - 10)))}");

        // 3b) 扫全部 Install 分组 img 的椅子总数（模拟修复后浏览器应加载的量）
        Console.WriteLine("=== 3b) 扫全部 Install 分组 img 椅子总数 ===");
        var allChairIds = new List<string>();
        if (installDir != null)
        {
            foreach (var c in installDir.Nodes)
            {
                if (!c.Text.EndsWith(".img")) { continue; }
                var subImg = c.GetValue<Wz_Image>();
                if (subImg == null || !subImg.TryExtract()) { continue; }
                foreach (Wz_Node child in subImg.Node.Nodes)
                {
                    if (int.TryParse(child.Text, out _) && child.Text.Length == 8)
                    {
                        allChairIds.Add(child.Text);
                    }
                }
            }
        }
        Console.WriteLine($"  Install 全部分组 img 椅子总数: {allChairIds.Count}");
        Console.WriteLine($"  前 10: {string.Join(",", allChairIds.Take(10))}");
        Console.WriteLine($"  末 10: {string.Join(",", allChairIds.Skip(Math.Max(0, allChairIds.Count - 10)))}");
        // 椅子 id 范围（MapleSalon2 isChairId：3010000 ≤ id < 3021000）
        var chairRange = allChairIds.Where(x => { var v = int.Parse(x); return v >= 3010000 && v < 3021000; }).ToList();
        Console.WriteLine($"  范围 3010000-3020999 内: {chairRange.Count} 个");
        Console.WriteLine($"  范围外前 10: {string.Join(",", allChairIds.Where(x => { var v = int.Parse(x); return v < 3010000 || v >= 3021000; }).Take(10))}");
        // 各分组文件包含的 id 范围
        if (installDir != null)
        {
            foreach (var c in installDir.Nodes)
            {
                if (!c.Text.EndsWith(".img")) { continue; }
                var subImg = c.GetValue<Wz_Image>();
                if (subImg == null || !subImg.TryExtract()) { continue; }
                var ids = new List<int>();
                foreach (Wz_Node child in subImg.Node.Nodes)
                {
                    if (int.TryParse(child.Text, out var idv) && child.Text.Length == 8) { ids.Add(idv); }
                }
                if (ids.Count > 0)
                {
                    Console.WriteLine($"    {c.Text}: {ids.Count} 个，范围 {ids.Min()}-{ids.Max()}");
                }
            }
        }

        // 4) 抽查椅子结构 + effect 出图（03010.img 内）
        Console.WriteLine("=== 4) 抽查椅子结构（前 3 个 + 指定 03010000/03010001）===");
        var samples = new List<string>();
        samples.AddRange(chairIds.Take(3));
        if (chairIds.Contains("03010000")) samples.Add("03010000");
        if (chairIds.Contains("03010001")) samples.Add("03010001");
        foreach (var id in samples.Distinct())
        {
            var chairNode = img10!.Node.FindNodeByPath(id);
            Console.WriteLine($"  --- {id} ---");
            if (chairNode == null)
            {
                Console.WriteLine($"    {id} 不存在");
                continue;
            }
            Console.WriteLine($"    子节点: {string.Join(",", chairNode.Nodes.Select(n => n.Text).Take(15))}");
            var info = chairNode.FindNodeByPath("info");
            if (info != null)
            {
                Console.WriteLine($"    info 子节点: {string.Join(",", info.Nodes.Select(n => n.Text).Take(20))}");
            }
            var effect = chairNode.FindNodeByPath("effect");
            if (effect == null)
            {
                Console.WriteLine($"    effect 不存在");
                continue;
            }
            Console.WriteLine($"    effect 子节点: {string.Join(",", effect.Nodes.Select(n => n.Text).Take(10))}");
            foreach (var f in effect.Nodes.Take(3))
            {
                var png = f.GetValue<Wz_Png>();
                var origin = f.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
                var uol = f.GetValue<Wz_Uol>();
                Console.WriteLine($"      {f.Text}: value={f.Value?.GetType().Name} png={(png != null ? $"{png.Width}x{png.Height}" : "无")} uol={(uol != null ? "有" : "无")} origin={(origin != null ? $"({origin.X},{origin.Y})" : "无")}");
            }
            // 出图 + 解析后尺寸/origin（ResolveToCanvas 会跳 outlink）
            var pngBytes = wz.ExtractPng($"Item/Install/03010.img/{id}/effect/0");
            var pngSize = wz.GetPngSize($"Item/Install/03010.img/{id}/effect/0");
            var effOrigin = wz.GetOrigin($"Item/Install/03010.img/{id}/effect/0");
            var effZ = wz.GetIntProperty($"Item/Install/03010.img/{id}/effect/z");
            Console.WriteLine($"    effect/0 出图: {(pngBytes != null ? $"{pngBytes.Length} 字节" : "无")} 尺寸={pngSize} origin=({effOrigin.x},{effOrigin.y}) z={effZ}");
            // icon 出图（列表缩略图用）
            var iconBytes = wz.ExtractPng($"Item/Install/03010.img/{id}/info/icon");
            Console.WriteLine($"    info/icon 出图: {(iconBytes != null ? $"{iconBytes.Length} 字节" : "无")}");
        }

        // 5) 名字从哪读：String/Ins.img（String 子节点有 Ins.img 而非 Item.img）
        Console.WriteLine("=== 5) 椅子名读取 ===");
        foreach (var id in samples.Take(2).Append("3010000").Append("3011000"))
        {
            var clean = id.TrimStart('0');
            if (clean.Length == 0) { continue; }
            Console.WriteLine($"  id={clean} GetItemName(Install): [{wz.GetItemName("Install", clean)}]");
            foreach (var p in new[]
            {
                $"String/Ins.img/Install/{clean}/name",
                $"String/Ins.img/{clean}/name",
                $"String/Ins.img/Install/{clean}",
            })
            {
                Console.WriteLine($"    {p}: [{wz.GetStringProperty(p)}]");
            }
        }
        // Ins.img 是 Wz_Image：TryExtract 后看结构
        var insImgNode = wz.FindNodeByPath("String/Ins.img");
        Console.WriteLine($"  String/Ins.img: {(insImgNode == null ? "不存在" : $"存在 value={insImgNode.Value?.GetType().Name}")}");
        var insImg = insImgNode?.GetValue<Wz_Image>();
        if (insImg != null && insImg.TryExtract())
        {
            Console.WriteLine($"    Ins.img 顶层: {string.Join(",", insImg.Node.Nodes.Select(n => n.Text).Take(30))}");
            foreach (var clean in new[] { "3010000", "3010001" })
            {
                var n301 = insImg.Node.FindNodeByPath($"Install/{clean}/name");
                var n301b = insImg.Node.FindNodeByPath($"{clean}/name");
                Console.WriteLine($"    {clean}: Install/{clean}/name=[{n301?.GetValueEx<string>(null)}] {clean}/name=[{n301b?.GetValueEx<string>(null)}]");
            }
        }

        // 5b) Cash 椅子（5204xxx）
        Console.WriteLine("=== 5b) Item/Cash 椅子 ===");
        var cashDir = wz.FindNodeByPath("Item/Cash");
        if (cashDir != null)
        {
            Console.WriteLine($"  Cash 子节点: {string.Join(",", cashDir.Nodes.Select(n => n.Text).Take(40))}");
            var cashAll = new List<string>();
            foreach (var c in cashDir.Nodes)
            {
                if (!c.Text.EndsWith(".img")) { continue; }
                var subImg = c.GetValue<Wz_Image>();
                if (subImg == null || !subImg.TryExtract()) { continue; }
                foreach (Wz_Node child in subImg.Node.Nodes)
                {
                    if (int.TryParse(child.Text, out var idv) && child.Text.Length == 8 && idv >= 5204000 && idv < 5205000)
                    {
                        cashAll.Add(child.Text);
                    }
                }
            }
            Console.WriteLine($"  Cash 5204xxx 椅子: {cashAll.Count} 个，前 10: {string.Join(",", cashAll.Take(10))}");
            foreach (var id in cashAll.Take(3))
            {
                var name = wz.GetStringProperty($"String/Ins.img/{id.TrimStart('0')}/name");
                Console.WriteLine($"    {id}: 名=[{name}] effect 出图=[{wz.ExtractPng($"Item/Cash/{id.Substring(0, 4)}.img/{id}/effect/0")?.Length} 字节]");
            }
        }
        else
        {
            Console.WriteLine("  Item/Cash 不存在");
        }

        // 6) 渲染路径核对：ChairRoot 探测（覆盖不同分组 img 的椅子，含修复候选 id/100 + Cash）
        Console.WriteLine("=== 6) ChairRoot 路径探测（渲染用）===");
        foreach (var id in new[] { "3010000", "3011000", "3010010", "3015000", "3015800", "3018600", "3019000", "3020000", "5204000", "5204001" })
        {
            var n = int.Parse(id);
            string node = n.ToString("D8");
            var hits = new List<string>();
            foreach (var c in new[]
            {
                $"Item/Install/{n / 1000:D5}.img/{node}",
                $"Item/Install/{n / 100:D6}.img/{node}",
                $"Item/Install/{n / 10000:D4}.img/{node}",
                $"Item/Cash/{n / 10000:D4}.img/{node}",
                $"Item/Cash/{n / 1000:D5}.img/{node}",
            })
            {
                if (wz.FindNodeByPath(c) != null) { hits.Add(c); }
            }
            Console.WriteLine($"    {id}: {(hits.Count > 0 ? string.Join(" | ", hits) : "全部未命中")}");
        }
    }
}
