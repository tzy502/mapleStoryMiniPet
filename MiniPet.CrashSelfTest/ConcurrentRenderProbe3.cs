using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>逐帧打点：定位动画卡顿发生的具体阶段。</summary>
public static class ConcurrentRenderProbe3
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

        paperdoll.SetAppearance(appA);
        paperdoll.Warmup();
        // 预热前基线帧
        var sw0 = Stopwatch.StartNew();
        var (f0, _, _, _, _) = paperdoll.RenderFrame(appA, "stand1", 0, "default");
        sw0.Stop();
        f0?.Dispose();
        Console.WriteLine($"预热前基线帧: {sw0.ElapsedMilliseconds}ms");

        var warmThread = new System.Threading.Thread(() => { paperdoll.WarmupFor(appB); });
        warmThread.Start();
        System.Threading.Thread.Sleep(50); // 让预热先跑 50ms（大概率已进入特效解码持锁）
        // 用 body stand1 真实帧数循环（真实 AnimService 按实际帧数驱动，不超界）
        int realFrames = 8;
        var bodyNode = wz.FindNodeByPath("Character/00002000.img/stand1");
        if (bodyNode != null)
        {
            int cnt = 0;
            foreach (var c in bodyNode.Nodes) { if (int.TryParse(c.Text, out _)) cnt++; }
            realFrames = cnt;
        }
        Console.WriteLine($"stand1 真实帧数: {realFrames}");
        for (int i = 0; i < realFrames; i++)
        {
            var sw = Stopwatch.StartNew();
            var (frm, _, _, _, _) = paperdoll.RenderFrame(appA, "stand1", i, "default");
            sw.Stop();
            frm?.Dispose();
            Console.WriteLine($"预热中帧 {i}: {sw.ElapsedMilliseconds}ms");
            System.Threading.Thread.Sleep(16);
        }
        warmThread.Join();
    }
}
