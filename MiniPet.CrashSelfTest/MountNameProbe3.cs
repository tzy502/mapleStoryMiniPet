using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>Taming 分类节点 id 格式与 name 实测。</summary>
public static class MountNameProbe3
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

        var taming = wz.FindNodeByPath("String/Eqp.img/Eqp/Taming");
        if (taming == null) { Console.WriteLine("Taming 不存在"); return; }
        Console.WriteLine($"Taming 节点数: {taming.Nodes.Count}");
        int shown = 0;
        foreach (var n in taming.Nodes)
        {
            if (shown++ >= 15) { break; }
            var nm = n.FindNodeByPath("name")?.GetValueEx<string>(null);
            Console.WriteLine($"  {n.Text}: {(nm ?? "无 name")}");
        }
        // 试多种 id 写法
        foreach (var key in new[] { "1902000", "01902000", "1902001", "01902001", "1930001", "01930001", "1932294", "01932294" })
        {
            var node = taming.FindNodeByPath(key);
            if (node != null)
            {
                var nm = node.FindNodeByPath("name")?.GetValueEx<string>(null);
                Console.WriteLine($"  Taming/{key}: 存在 name={(nm ?? "无")}");
            }
        }
        // Character/TamingMob img info/name
        foreach (var id in new[] { "1902000", "1930001", "1932294" })
        {
            var img = wz.FindNodeByPath($"Character/TamingMob/{id.PadLeft(8, '0')}.img/info/name");
            var nm = img?.GetValueEx<string>(null);
            Console.WriteLine($"  TamingMob img {id} info/name: {(nm ?? "无")}");
        }
    }
}
