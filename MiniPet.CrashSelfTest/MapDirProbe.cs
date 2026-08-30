using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>口径核对：Map/Map 全量 img 数 vs ListMapIds。</summary>
public static class MapDirProbe
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

        var root = wz.FindNodeByPath("Map");
        int total = 0;
        if (root != null)
        {
            foreach (var grp in root.Nodes)
            {
                if (grp.Text == "Map")
                {
                    int subtotal = 0;
                    foreach (var sub in grp.Nodes)
                    {
                        int cnt = 0;
                        foreach (var c in sub.Nodes)
                        {
                            if (c.Text.EndsWith(".img")) { cnt++; }
                        }
                        Console.WriteLine($"  Map/{sub.Text}: {cnt} img");
                        subtotal += cnt;
                    }
                    Console.WriteLine($"  Map 组小计 = {subtotal}");
                    total += subtotal;
                }
                else
                {
                    int cnt = 0;
                    foreach (var c in grp.Nodes)
                    {
                        if (c.Text.EndsWith(".img")) { cnt++; }
                    }
                    Console.WriteLine($"  {grp.Text}: {cnt} img");
                    total += cnt;
                }
            }
        }
        Console.WriteLine($"Map.wz 全量 = {total}");
        Console.WriteLine($"ListMapIds = {wz.ListMapIds().Count}");
    }
}
