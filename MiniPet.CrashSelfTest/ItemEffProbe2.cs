using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>特效帧结构细节：Effect/ItemEff.img/1102149/effect 的 z 值、帧 canvas 出图、动作结构。</summary>
public static class ItemEffProbe2
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

        var root = "Effect/ItemEff.img/1102149/effect";
        var eff = wz.FindNodeByPath(root);
        if (eff == null) { Console.WriteLine("effect 不存在"); return; }

        Console.WriteLine("=== 顶层子节点 ===");
        foreach (var c in eff.Nodes) Console.WriteLine($"  {c.Text}");

        // fixed 结构
        Console.WriteLine("=== fixed ===");
        var fixedNode = eff.FindNodeByPath("fixed");
        if (fixedNode != null)
        {
            foreach (var c in fixedNode.Nodes)
            {
                var z = c.FindNodeByPath("z")?.GetValueEx<int?>(null);
                Console.WriteLine($"  {c.Text} z={(z.HasValue ? z.Value.ToString() : "?")}");
            }
            // 帧 0 出图（找 canvas 位置）
            foreach (var path in new[] { "fixed/0", "fixed/0/canvas", "fixed/0/0" })
            {
                var png = wz.ExtractPng($"{root}/{path}");
                Console.WriteLine($"  {path} 出图: {png != null} ({(png != null ? $"{png.Length}B" : "无")})");
            }
            // fixed/0 子节点列表
            var f0 = fixedNode.FindNodeByPath("0");
            if (f0 != null)
            {
                foreach (var c in f0.Nodes)
                {
                    Console.WriteLine($"    fixed/0/{c.Text} value={c.Value?.GetType().Name}");
                }
            }
        }

        // default 帧出图（default/0）
        foreach (var path in new[] { "default/0", "default/1", "ladder/0" })
        {
            var png = wz.ExtractPng($"{root}/{path}");
            Console.WriteLine($"  {path} 出图: {png != null} ({(png != null ? $"{png.Length}B" : "无")})");
        }
        // 其他披风对比：1102000 有没有特效
        foreach (var id in new[] { "1102000", "1102081", "1103488" })
        {
            var e2 = wz.FindNodeByPath($"Effect/ItemEff.img/{id}/effect");
            Console.WriteLine($"  {id} effect: {(e2 != null ? "存在" : "无")}");
        }
    }
}
