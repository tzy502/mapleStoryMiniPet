using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>WarmupFor 验证：后台预热新外观（不 SetAppearance），动画线程旧外观全程不卡。</summary>
public static class ConcurrentRenderProbe2
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
        var appB = new CharacterAppearance { EnableEffect = true, Cap = new ItemInfo { Id = "1003953" } };
        appB.Hair = appA.Hair; appB.Face = appA.Face;

        // 暖 A 的 stand1（模拟启动预热）
        paperdoll.SetAppearance(appA);
        paperdoll.Warmup();

        // 后台：WarmupFor(appB)（模拟新 ApplyPaperdoll 流程，不 SetAppearance）
        var warmThread = new System.Threading.Thread(() => { paperdoll.WarmupFor(appB); });
        warmThread.Start();

        // 动画线程：预热期间旧外观 20 帧（应全快）
        var maxMs = 0L;
        var totalMs = 0L;
        for (int i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            var (frm, _, _, _, _) = paperdoll.RenderFrame(appA, "stand1", i % 8, "default");
            frm?.Dispose();
            sw.Stop();
            maxMs = Math.Max(maxMs, sw.ElapsedMilliseconds);
            totalMs += sw.ElapsedMilliseconds;
            System.Threading.Thread.Sleep(16);
        }
        warmThread.Join();
        Console.WriteLine($"预热期间旧外观动画 20 帧: 总 {totalMs}ms 最大单帧 {maxMs}ms（要求 <100ms）");
        // 切换后首帧（新外观已热）
        paperdoll.SetAppearance(appB);
        var sw2 = Stopwatch.StartNew();
        var (frm2, _, _, _, _) = paperdoll.RenderFrame(appB, "stand1", 0, "default");
        sw2.Stop();
        frm2?.Dispose();
        Console.WriteLine($"切换后首帧: {sw2.ElapsedMilliseconds}ms（要求 <100ms）");
    }
}
