using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>「黄色电」开头发型探测：String.wz Hair 分类名字匹配 + GetItemName 命中对比。</summary>
public static class YellowHairProbe
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

        // 1) String/Eqp.img/Eqp/Hair 分类下名字含「黄」的发型
        Console.WriteLine("=== String Hair 分类含「黄」的发型 ===");
        var hairCat = wz.FindNodeByPath("String/Eqp.img/Eqp/Hair");
        var found = new List<(string Id, string Name)>();
        if (hairCat != null)
        {
            var root = hairCat;
            var img = hairCat.GetValue<Wz_Image>();
            if (img != null && img.TryExtract()) { root = img.Node; }
            foreach (var idNode in root.Nodes)
            {
                var name = idNode.FindNodeByPath("name")?.GetValueEx<string>(null);
                if (name != null && name.Contains("黄"))
                {
                    found.Add((idNode.Text, name));
                    Console.WriteLine($"  {idNode.Text} → {name}");
                    if (found.Count >= 30) { break; }
                }
            }
            Console.WriteLine($"  共匹配（前 30 展示）: {found.Count}");
        }
        else
        {
            Console.WriteLine("  String/Eqp.img/Eqp/Hair 不存在");
        }

        // 2) GetItemName 对这些 id 的命中情况（素材浏览器实际显示名）
        Console.WriteLine("=== GetItemName 命中对比 ===");
        var misses = new List<(string Id, string Name)>();
        foreach (var (id, name) in found.Take(15))
        {
            var cn = wz.GetItemName("Hair", id);
            bool okHit = cn == name;
            Console.WriteLine($"  {id}: String={name} GetItemName={(string.IsNullOrEmpty(cn) ? "空" : cn)} {(okHit ? "✓" : "✗ 不一致")}");
            if (!okHit) { misses.Add((id, name)); }
        }
        Console.WriteLine($"  不一致/缺失: {misses.Count} 个");
    }
}
