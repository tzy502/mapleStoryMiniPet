using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>无竞争时动画帧逐帧耗时（定位帧 1 是否自身冷查询）。</summary>
public static class FrameColdProbe
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

        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));
        var appA = new CharacterAppearance();
        foreach (var id in new[] { "30000", "31000" })
        {
            if (wz.FindNodeByPath($"Character/Hair/{id.PadLeft(8, '0')}.img") != null) { appA.Hair = new ItemInfo { Id = id }; break; }
        }
        foreach (var id in new[] { "20000", "21000" })
        {
            if (wz.FindNodeByPath($"Character/Face/{id.PadLeft(8, '0')}.img") != null) { appA.Face = new ItemInfo { Id = id }; break; }
        }
        paperdoll.SetAppearance(appA);
        paperdoll.Warmup(); // 启动预热（bounds+move+位图）
        // 无竞争逐帧
        for (int i = 0; i < 8; i++)
        {
            var sw = Stopwatch.StartNew();
            var (frm, _, _, _, _) = paperdoll.RenderFrame(appA, "stand1", i, "default");
            sw.Stop();
            frm?.Dispose();
            Console.WriteLine($"帧 {i}: {sw.ElapsedMilliseconds}ms");
        }
    }
}
