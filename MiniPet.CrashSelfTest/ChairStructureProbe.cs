using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 椅子 WZ 结构探针（对照 MapleSalon2 chair.ts 的 Chair.img/{id} 解析）：
/// 抽查 5 把椅子的顶层节点 / info 内容 / effect 结构（帧节点类型、origin/z/delay）、
/// effect2/effect3/randEffect/customChair 等 MapleSalon2 解析所需节点是否存在。
/// 用法：dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.ChairStructureProbe
/// </summary>
public static class ChairStructureProbe
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
                Console.WriteLine(ex.StackTrace);
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

        var ids = new[] { "3010000", "3010001", "3011000", "3015000", "3020000" };
        foreach (var id in ids)
        {
            string root = FindChairRoot(wz, id);
            Console.WriteLine($"\n=== 椅子 {id} → {root} ===");
            if (root.Length == 0)
            {
                Console.WriteLine("  根路径未命中");
                continue;
            }

            // ① 顶层节点
            Console.WriteLine("  顶层节点:");
            DumpChildren(wz, root, 2);

            // ② info 内容
            var infoPath = $"{root}/info";
            Console.WriteLine($"  info 子节点 (name=value):");
            DumpInfo(wz, infoPath);

            // ③ effect 结构
            Console.WriteLine("  effect 结构:");
            DumpEffect(wz, $"{root}/effect");

            // ④ effect2/effect3/randEffect/customChair 等
            foreach (var extra in new[] { "effect2", "effect3", "randEffect", "customEffect", "seteffect" })
            {
                var extraPath = $"{root}/{extra}";
                bool exists = wz.WithWzLock(() => wz.FindNodeByPath(extraPath) != null);
                Console.WriteLine($"  {extra}: {(exists ? "存在" : "无")}");
                if (exists && (extra == "effect2" || extra == "effect3"))
                {
                    DumpEffect(wz, extraPath);
                }
            }
            var customChair = wz.WithWzLock(() => wz.FindNodeByPath($"{root}/info/customChair"));
            Console.WriteLine($"  info/customChair: {(customChair == null ? "无" : "存在")}");
            if (customChair != null)
            {
                foreach (Wz_Node c in customChair.Nodes)
                {
                    Console.WriteLine($"    customChair.{c.Text}: value={ValueStr(c)}");
                }
            }
        }

        // ⑤ 坐姿角色动作帧数（评估「角色坐姿」是否可用——body 是否有 sit 动作）
        Console.WriteLine("\n=== 角色 sit 动作帧数（00002000.img / 00012000.img）===");
        foreach (var bodyRoot in new[] { "Character/00002000.img", "Character/00012000.img" })
        {
            int sitFrames = CountFrames(wz, $"{bodyRoot}/sit");
            int standFrames = CountFrames(wz, $"{bodyRoot}/stand1");
            Console.WriteLine($"  {bodyRoot}: sit={sitFrames} stand1={standFrames}");
        }

        // ⑥ 3014000 结构深挖（effect/0 为 1x1 但 info 有 sitAction/bodyRelMove——本体图像在哪？）
        Console.WriteLine("\n=== 3014000 结构深挖（1x1 effect 但有 sitAction）===");
        var deepRoot = FindChairRoot(wz, "3014000");
        if (deepRoot.Length > 0)
        {
            Console.WriteLine($"  {deepRoot}");
            DumpChildren(wz, deepRoot, 20);
            DumpInfo(wz, $"{deepRoot}/info");
            DumpEffect(wz, $"{deepRoot}/effect");
            // 顶层每个非 info/effect 节点都列出来
            wz.WithWzLock(() =>
            {
                var node = wz.FindNodeByPath(deepRoot);
                if (node == null) return true;
                foreach (Wz_Node c in node.Nodes)
                {
                    if (c.Text == "info" || c.Text == "effect") continue;
                    Console.WriteLine($"  顶层 {c.Text}: value={ValueStr(c)} 子节点={c.Nodes.Select(n => n.Text).Take(8).ToList().Count}");
                    foreach (Wz_Node cc in c.Nodes.Take(8))
                    {
                        Console.WriteLine($"    {c.Text}/{cc.Text}: value={ValueStr(cc)}");
                    }
                }
                return true;
            });
        }

        // ⑦ 全量扫描：多少椅子带 effect2/effect3/randEffect/自定义节点（评估是否需要支持多 effect 层）
        Console.WriteLine("\n=== 全量扫描 effect2/effect3 覆盖率 ===");
        int total = 0, hasEff2 = 0, hasEff3 = 0, hasRand = 0, hasSetEffect = 0, hasMultiFrame = 0;
        foreach (var group in new[] { "03010.img", "03011.img", "03014.img", "03015.img", "03016.img", "03017.img", "03018.img", "03019.img", "030150.img", "030158.img", "0302.img", "0301.img" })
        {
            var children = wz.GetImgChildren($"Item/Install/{group}");
            foreach (var id in children)
            {
                if (!int.TryParse(id, out var v) || v < 3010000 || v >= 3021000) continue;
                total++;
                string node = v.ToString("D8");
                wz.WithWzLock(() =>
                {
                    var chairNode = wz.FindNodeByPath($"Item/Install/{group}/{node}");
                    if (chairNode == null) return true;
                    foreach (Wz_Node c in chairNode.Nodes)
                    {
                        if (c.Text == "effect2") hasEff2++;
                        else if (c.Text == "effect3") hasEff3++;
                        else if (c.Text == "randEffect") hasRand++;
                        else if (c.Text == "seteffect") hasSetEffect++;
                    }
                    var effect = chairNode.FindNodeByPath("effect");
                    if (effect != null)
                    {
                        int frames = 0;
                        foreach (Wz_Node f in effect.Nodes)
                        {
                            if (int.TryParse(f.Text, out _)) frames++;
                        }
                        if (frames > 1) hasMultiFrame++;
                    }
                    return true;
                });
            }
        }
        Console.WriteLine($"  椅子总数(已扫分组): {total}");
        Console.WriteLine($"  带 effect2: {hasEff2} ({(total > 0 ? 100.0 * hasEff2 / total : 0):F1}%)");
        Console.WriteLine($"  带 effect3: {hasEff3}");
        Console.WriteLine($"  带 randEffect: {hasRand}");
        Console.WriteLine($"  带 seteffect: {hasSetEffect}");
        Console.WriteLine($"  effect 多帧(>1): {hasMultiFrame}");
    }

    /// <summary>ChairRoot 同款路径探测（渲染链路用同一逻辑）。</summary>
    private static string FindChairRoot(WzService wz, string id)
    {
        int n = int.Parse(id);
        string node = n.ToString("D8");
        string[] candidates =
        {
            $"Item/Install/{n / 1000:D5}.img/{node}",
            $"Item/Install/{n / 100:D6}.img/{node}",
            $"Item/Install/{n / 10000:D4}.img/{node}",
            $"Item/Install/0301.img/{node}",
            $"Item/Cash/{n / 1000:D5}.img/{node}",
            $"Item/Cash/{n / 100:D6}.img/{node}",
            $"Item/Cash/{n / 10000:D4}.img/{node}",
        };
        foreach (var c in candidates)
        {
            if (wz.WithWzLock(() => wz.FindNodeByPath(c) != null)) return c;
        }
        return "";
    }

    private static void DumpChildren(WzService wz, string path, int max)
    {
        wz.WithWzLock(() =>
        {
            var node = wz.FindNodeByPath(path);
            if (node == null)
            {
                Console.WriteLine($"    {path} → 不存在");
                return true;
            }
            var names = node.Nodes.Select(n => n.Text).ToList();
            Console.WriteLine($"    {names.Count} 个子节点: {string.Join(",", names.Take(max))}");
            if (names.Count > max)
            {
                Console.WriteLine($"    更多: {string.Join(",", names.Skip(max).Take(10))}");
            }
            return true;
        });
    }

    private static string ValueStr(Wz_Node n)
    {
        var v = n.Value;
        if (v == null) return "(null)";
        if (v is Wz_Vector vec) return $"({vec.X},{vec.Y})";
        if (v is string s) return $"\"{s}\"";
        if (v is int i) return i.ToString();
        return v.GetType().Name;
    }

    private static void DumpInfo(WzService wz, string infoPath)
    {
        wz.WithWzLock(() =>
        {
            var info = wz.FindNodeByPath(infoPath);
            if (info == null)
            {
                Console.WriteLine("    info 不存在");
                return true;
            }
            foreach (Wz_Node c in info.Nodes)
            {
                string v = ValueStr(c);
                if (v.Length > 60) v = v.Substring(0, 60) + "…";
                Console.WriteLine($"    {c.Text} = {v}");
            }
            return true;
        });
    }

    /// <summary>effect 节点结构：帧数、每帧类型（png/uol/容器）、origin/z/delay。</summary>
    private static void DumpEffect(WzService wz, string effectPath)
    {
        wz.WithWzLock(() =>
        {
            var effect = wz.FindNodeByPath(effectPath);
            if (effect == null)
            {
                Console.WriteLine($"    {effectPath} → 不存在");
                return true;
            }
            var names = effect.Nodes.Select(n => n.Text).ToList();
            Console.WriteLine($"    帧节点: {names.Count} 个: {string.Join(",", names.Take(12))}{(names.Count > 12 ? "…" : "")}");
            foreach (Wz_Node f in effect.Nodes.Take(4))
            {
                var v = f.Value;
                string type = v == null ? "(null/容器)" : v.GetType().Name;
                var png = f.GetValue<Wz_Png>();
                var uol = f.GetValue<Wz_Uol>();
                var origin = f.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
                var zNode = f.FindNodeByPath("z")?.GetValueEx<int>(0);
                var delay = f.FindNodeByPath("delay")?.GetValueEx<int>(0);
                string extra = "";
                if (png != null) extra = $" png={png.Width}x{png.Height}";
                if (uol != null) extra = $" uol→{uol.Uol}";
                if (origin != null) extra += $" origin=({origin.X},{origin.Y})";
                if (zNode != null && zNode != 0) extra += $" z={zNode}";
                if (delay != null && delay != 0) extra += $" delay={delay}";
                Console.WriteLine($"    {f.Text}: {type}{extra}");
                // 容器（Wz_Png 是 value 直接在节点，非容器）——打印子节点
                if (v == null)
                {
                    var sub = f.Nodes.Select(s => s.Text).Take(6).ToList();
                    Console.WriteLine($"      子节点: {string.Join(",", sub)}");
                }
            }
            // 顶层 z/delay（WzChairEffectItem 的 z/delay 在 effect 节点本身上）
            var effZ = effect.FindNodeByPath("z")?.GetValueEx<int>(0);
            var effDelay = effect.FindNodeByPath("delay")?.GetValueEx<int>(0);
            if ((effZ != null && effZ != 0) || (effDelay != null && effDelay != 0))
            {
                Console.WriteLine($"    effect 节点自身: z={effZ} delay={effDelay}");
            }
            return true;
        });
    }

    private static int CountFrames(WzService wz, string actionPath)
    {
        try
        {
            return wz.WithWzLock(() =>
            {
                var node = wz.FindNodeByPath(actionPath);
                if (node == null) return 0;
                int count = 0;
                foreach (Wz_Node child in node.Nodes)
                {
                    if (int.TryParse(child.Text, out _)) count++;
                }
                return count;
            });
        }
        catch { return 0; }
    }

    private static int CountNumericChildren(WzService wz, string path)
    {
        try
        {
            return wz.WithWzLock(() =>
            {
                var node = wz.FindNodeByPath(path);
                if (node == null) return 0;
                int count = 0;
                foreach (Wz_Node child in node.Nodes)
                {
                    if (int.TryParse(child.Text, out _)) count++;
                }
                return count;
            });
        }
        catch { return 0; }
    }
}
