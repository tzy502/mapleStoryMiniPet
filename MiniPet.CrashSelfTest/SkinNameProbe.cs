using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 皮肤名字探测：对照 MapleSalon2 后端（String/Equip.img/Eqp/{分类}/{id}/name），
/// 实测本 WZ 里皮肤（Body 2000-2999）的名字真实位置，确认 TryGetSkinName 各级路径是否命中。
/// </summary>
public static class SkinNameProbe
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

        // 1) Character 目录下 2000-2999 的 Body id
        Console.WriteLine("=== Character 目录 Body id（2000-2999）===");
        var bodies = new List<int>();
        foreach (var (id, _) in wz.GetDirectoryChildren("Character"))
        {
            if (int.TryParse(id, out var b) && b >= 2000 && b <= 2999)
            {
                bodies.Add(b);
            }
        }
        bodies.Sort();
        Console.WriteLine($"  Body id: [{string.Join(",", bodies)}]");

        // 2) 各级名字探测
        foreach (var b in bodies)
        {
            var idStr = b.ToString();
            var pad = b.ToString("D8");
            string? n1 = null, n2 = null, n3 = null;
            // 一级：Character/Body.img/{id}/info/name
            var bodyImg = wz.FindNodeByPath("Character/Body.img");
            if (bodyImg != null)
            {
                var root = bodyImg;
                var img = bodyImg.GetValue<Wz_Image>();
                if (img != null && img.TryExtract()) { root = img.Node; }
                foreach (var key in new[] { idStr, pad })
                {
                    n1 = root.FindNodeByPath($"{key}/info/name")?.GetValueEx<string>(null);
                    if (!string.IsNullOrEmpty(n1)) { break; }
                }
            }
            // 二级：String/Eqp.img/Eqp/Skin/{id}/name（含 Equip.img 别名尝试）
            foreach (var imgName in new[] { "Eqp.img", "Equip.img" })
            {
                var skinImg = wz.FindNodeByPath($"String/{imgName}");
                if (skinImg == null) { continue; }
                var sRoot = skinImg;
                var sImg = skinImg.GetValue<Wz_Image>();
                if (sImg != null && sImg.TryExtract()) { sRoot = sImg.Node; }
                foreach (var key in new[] { idStr, pad })
                {
                    n2 = sRoot.FindNodeByPath($"Eqp/Skin/{key}/name")?.GetValueEx<string>(null);
                    if (!string.IsNullOrEmpty(n2)) { break; }
                }
                if (!string.IsNullOrEmpty(n2)) { break; }
            }
            // 三级：GetItemName 通用
            n3 = wz.GetItemName("Body", idStr);
            Console.WriteLine($"  {b}: Body.img={n1 ?? "无"} Eqp/Skin={n2 ?? "无"} GetItemName={n3 ?? "无"}");
        }

        // 3) String/Eqp.img/Eqp 下有哪些分类（确认 Skin 分类存在与否）
        Console.WriteLine("=== String/Eqp.img/Eqp 分类 ===");
        var eqpRoot = wz.FindNodeByPath("String/Eqp.img");
        if (eqpRoot != null)
        {
            var eRoot = eqpRoot;
            var eImg = eqpRoot.GetValue<Wz_Image>();
            if (eImg != null && eImg.TryExtract()) { eRoot = eImg.Node; }
            var eqp = eRoot.FindNodeByPath("Eqp");
            if (eqp != null)
            {
                foreach (var c in eqp.Nodes)
                {
                    Console.WriteLine($"  {c.Text}");
                }
                // Skin 分类实际内容
                var skinCat = eqp.FindNodeByPath("Skin");
                if (skinCat != null)
                {
                    Console.WriteLine("=== Skin 分类节点（前 30 个）===");
                    var count = 0;
                    foreach (var s in skinCat.Nodes)
                    {
                        if (count++ >= 30) { break; }
                        var nm = s.FindNodeByPath("name")?.GetValueEx<string>(null);
                        Console.WriteLine($"  {s.Text} → {(nm ?? "无 name")}");
                    }
                    Console.WriteLine($"  Skin 分类共 {(count >= 30 ? "≥30" : count.ToString())} 个节点（前 30 展示）");
                }
                else
                {
                    Console.WriteLine("  Skin 分类不存在");
                }
            }
            else
            {
                Console.WriteLine("  Eqp 子节点不存在");
            }
        }
        else
        {
            Console.WriteLine("  String/Eqp.img 不存在");
        }
    }
}
