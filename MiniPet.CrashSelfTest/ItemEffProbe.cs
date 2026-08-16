using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>装备特效探测：MapleSalon2 特效路径 = Effect/ItemEff.img/{id}/effect（非 Character/Cape.img）。</summary>
public static class ItemEffProbe
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

        // 1) Effect/ItemEff.img 存在性 + 1102149 的几种 id 写法
        Console.WriteLine("=== Effect/ItemEff.img 检查 ===");
        var effImg = wz.FindNodeByPath("Effect/ItemEff.img");
        Console.WriteLine($"  Effect/ItemEff.img 存在: {effImg != null}");
        if (effImg != null)
        {
            foreach (var key in new[] { "1102149", "01102149", "11021490" })
            {
                var node = effImg.FindNodeByPath(key);
                Console.WriteLine($"  {key}: {(node != null ? "存在" : "无")}");
                if (node != null)
                {
                    var eff = node.FindNodeByPath("effect");
                    Console.WriteLine($"    effect 子节点: {eff != null}");
                    if (eff != null)
                    {
                        foreach (var e in eff.Nodes)
                        {
                            Console.WriteLine($"      {e.Text}");
                            if (e.Nodes.Count > 0)
                            {
                                foreach (var e2 in e.Nodes) { Console.WriteLine($"        {e2.Text}"); }
                            }
                        }
                    }
                }
            }
            // 顺带看 ItemEff.img 下有没有 110xxxx 系列（确认 id 位数约定）
            int sample = 0;
            foreach (var c in effImg.Nodes)
            {
                if (c.Text.StartsWith("110") && sample++ < 5)
                {
                    Console.WriteLine($"  样例 110 系: {c.Text}");
                }
            }
        }
    }
}
