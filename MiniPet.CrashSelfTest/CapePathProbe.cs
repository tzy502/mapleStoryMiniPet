using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>中文名搜索披风：String.wz 里找「冰凌」对应装备，确认 id/路径/effect 结构。</summary>
public static class CapePathProbe
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

        // 1) 搜索 String/Eqp.img 里名字含「冰凌」的节点（披风名所在）
        // 结构：Eqp.img/Eqp/{分类如 Cape}/{id}/name
        Console.WriteLine("=== 搜 String.wz 含「冰凌」的装备名 ===");
        var eqp = wz.FindNodeByPath("String/Eqp.img");
        if (eqp != null)
        {
            foreach (var catNode in eqp.Nodes)
            {
                // catNode.Text 如 "Eqp"；其下再分 Cape/Coat... 
                foreach (var subNode in catNode.Nodes)
                {
                    // subNode 可能是分类（Cape）或直接 id；先查自身，再遍历其 id 子节点
                    var selfName = subNode.FindNodeByPath("name")?.GetValueEx<string>(null);
                    if (selfName != null && selfName.Contains("冰凌"))
                    {
                        Console.WriteLine($"  [{catNode.Text}/{subNode.Text}] {subNode.Text} → {selfName}");
                    }
                    foreach (var idNode in subNode.Nodes)
                    {
                        var nameNode = idNode.FindNodeByPath("name");
                        var name = nameNode?.GetValueEx<string>(null);
                        if (name != null && name.Contains("冰凌"))
                        {
                            Console.WriteLine($"  [{catNode.Text}/{subNode.Text}] {idNode.Text} → {name}");
                        }
                    }
                }
            }
        }
        else
        {
            Console.WriteLine("  String/Eqp.img 不存在");
        }

        // 2) 验证 1102149 的 Cape.img 路径 + effect 结构
        Console.WriteLine("=== 验证 1102149 Cape.img 路径与 effect ===");
        var candidates = new[]
        {
            "Character/Cape/01102149.img",
            "Character/Cape/0110249.img",
            "Character/Cape/01102490.img",
            "Character/Cape/1102149.img",
        };        foreach (var p in candidates)
        {
            var node = wz.FindNodeByPath(p);
            if (node == null)
            {
                Console.WriteLine($"  {p} → 不存在");
                continue;
            }
            Console.WriteLine($"  {p} → 存在");
            // 列出顶层子节点，找 effect 实际位置
            foreach (var top in node.Nodes)
            {
                Console.WriteLine($"    顶层: {top.Text}");
                var eff2 = top.FindNodeByPath("effect");
                if (eff2 != null)
                {
                    Console.WriteLine($"      → {top.Text}/effect 存在");
                    foreach (var e in eff2.Nodes)
                    {
                        Console.WriteLine($"        {e.Text}");
                    }
                }
                // 也查 default 下的 effect
                var dflt = top.FindNodeByPath("default");
                if (dflt != null)
                {
                    foreach (var d in dflt.Nodes)
                    {
                        if (d.Text.StartsWith("effect"))
                        {
                            Console.WriteLine($"      → {top.Text}/default/{d.Text} 存在");
                        }
                    }
                }
            }
            // info 节点内容
            var infoNode = node.FindNodeByPath("info");
            if (infoNode != null)
            {
                Console.WriteLine("    info 子节点:");
                foreach (var i in infoNode.Nodes)
                {
                    Console.WriteLine($"      {i.Text}");
                }
            }
            break;
        }

        // 3) 扫描 Cape 目录：哪些披风带 effect（含节点级或 info 级）
        Console.WriteLine("=== 扫 Cape 目录找带 effect 的披风（前 20 个）===");
        var capeDir = wz.FindNodeByPath("Character/Cape");
        var withEff = 0;
        var checkedCount = 0;
        if (capeDir != null)
        {
            foreach (var imgNode in capeDir.Nodes)
            {
                checkedCount++;
                if (!imgNode.Text.EndsWith(".img")) continue;
                var hasEff = imgNode.FindNodeByPath("effect") != null
                    || imgNode.FindNodeByPath("info/effect") != null;
                if (hasEff)
                {
                    withEff++;
                    if (withEff <= 20)
                    {
                        Console.WriteLine($"  {imgNode.Text}");
                    }
                }
            }
        }
        Console.WriteLine($"  共扫 {checkedCount} 个 img，带 effect 的: {withEff}");

        // 4) 验证 1102149 本体出图（stand1 首帧）
        Console.WriteLine("=== 验证 1102149 出图 ===");
        var capeRoot = "Character/Cape/01102149.img";
        var capeNode2 = wz.FindNodeByPath(capeRoot);
        // 帧真实路径 = {action}/{frame}/cape；PNG 尺寸从头部解析（16-19 宽 / 20-23 高，大端）
        foreach (var action in new[] { "stand1", "stand2", "walk1", "alert", "jump" })
        {
            var png = wz.ExtractPng($"{capeRoot}/{action}/0/cape");
            string dim = "?";
            if (png != null && png.Length > 24)
            {
                var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
                var h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
                dim = $"{w}x{h}";
            }
            Console.WriteLine($"  {action}/0/cape 出图: {png != null} ({(png != null ? $"{png.Length} 字节 {dim}" : "无")})");
        }
        // 对照：任意 3 件普通披风结构（帧里是否也有 cape 子节点）
        foreach (var ctrl in new[] { "Character/Cape/01102000.img", "Character/Cape/01102001.img", "Character/Cape/01102002.img" })
        {
            var capeNode3 = wz.FindNodeByPath(ctrl);
            if (capeNode3 == null)
            {
                Console.WriteLine($"  对照 {ctrl} → 不存在");
                continue;
            }
            var s0 = capeNode3.FindNodeByPath("stand1/0");
            Console.WriteLine($"  对照 {ctrl} stand1/0 子节点:");
            if (s0 != null)
            {
                foreach (var kid in s0.Nodes)
                {
                    var p = kid.GetValue<Wz_Png>();
                    Console.WriteLine($"    {kid.Text} value={kid.Value?.GetType().Name} png={(p != null ? $"{p.Width}x{p.Height}" : "无")}");
                }
                var png0 = wz.ExtractPng($"{ctrl}/stand1/0/cape");
                Console.WriteLine($"    stand1/0/cape 出图: {png0 != null} ({(png0 != null ? $"{png0.Length} 字节" : "无")})");
            }
        }
    }
}
