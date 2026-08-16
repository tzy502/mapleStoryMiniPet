using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>并发锁验证：后台预热（新外观冷加载）期间，动画线程 RenderFrame 的阻塞时间（修复前 = 预热全程 4.5s）。</summary>
public static class ConcurrentRenderProbe
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
        // 外观 A：普通（预热）
        var appA = new CharacterAppearance();
        foreach (var id in new[] { "30000", "31000" })
        {
            if (wz.FindNodeByPath($"Character/Hair/{id.PadLeft(8, '0')}.img") != null) { appA.Hair = new ItemInfo { Id = id }; break; }
        }
        foreach (var id in new[] { "20000", "21000" })
        {
            if (wz.FindNodeByPath($"Character/Face/{id.PadLeft(8, '0')}.img") != null) { appA.Face = new ItemInfo { Id = id }; break; }
        }
        // 外观 B：神之子头冠（特效冷加载）
        var appB = new CharacterAppearance { EnableEffect = true, Cap = new ItemInfo { Id = "1003953" } };
        appB.Hair = appA.Hair; appB.Face = appA.Face;

        // 先暖 A（动画侧基础）
        paperdoll.CompositeFrame(appA, "stand1", 0);

        // 后台预热 B（模拟 ApplyPaperdoll 后台 Task）
        var warmThread = new System.Threading.Thread(() =>
        {
            paperdoll.SetAppearance(appB);
            paperdoll.Warmup();
        });
        warmThread.Start();

        // 动画线程（UI 线程模拟）：预热期间连跑 20 帧，测每帧耗时
        var maxMs = 0L;
        var totalMs = 0L;
        for (int i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            var (frmA, _, _, _, _) = paperdoll.RenderFrame(appA, "stand1", i % 4, "default"); frmA?.Dispose();
            sw.Stop();
            maxMs = Math.Max(maxMs, sw.ElapsedMilliseconds);
            totalMs += sw.ElapsedMilliseconds;
            System.Threading.Thread.Sleep(16);
        }
        warmThread.Join();
        Console.WriteLine($"预热期间动画 20 帧: 总 {totalMs}ms 最大单帧 {maxMs}ms（修复前 = 首帧阻塞 ~4.5s）");
        // 交换后动画（新外观已热）
        paperdoll.SetAppearance(appB);
        var sw2 = Stopwatch.StartNew();
        var (frmB, _, _, _, _) = paperdoll.RenderFrame(appB, "stand1", 0, "default"); frmB?.Dispose();
        sw2.Stop();
        Console.WriteLine($"换装后首帧: {sw2.ElapsedMilliseconds}ms");
    }
}
