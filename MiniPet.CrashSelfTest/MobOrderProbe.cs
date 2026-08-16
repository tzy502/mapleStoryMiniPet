using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>怪物/NPC 列表顺序探测：ListMobIds/ListNpcIds 返回顺序。</summary>
public static class MobOrderProbe
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

        var mobs = wz.ListMobIds();
        Console.WriteLine($"Mob 总数: {mobs.Count}");
        Console.WriteLine($"Mob 前 15: {string.Join(",", mobs.Take(15))}");
        Console.WriteLine($"Mob 是否升序: {mobs.SequenceEqual(mobs.OrderBy(x => long.Parse(x)))}");
        var npcs = wz.ListNpcIds();
        Console.WriteLine($"NPC 总数: {npcs.Count}");
        Console.WriteLine($"NPC 前 15: {string.Join(",", npcs.Take(15))}");
        Console.WriteLine($"NPC 是否升序: {npcs.SequenceEqual(npcs.OrderBy(x => long.Parse(x)))}");
    }
}
