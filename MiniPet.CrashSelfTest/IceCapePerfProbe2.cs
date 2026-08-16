using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>冰凌披风耗时分解：WZ 冷访问 vs 合成 vs 绘制。</summary>
public static class IceCapePerfProbe2
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
        var sw = Stopwatch.StartNew();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath ?? wzConfig.BaseWzPath);
        sw.Stop();
        Console.WriteLine($"WZ LoadWz: {sw.ElapsedMilliseconds}ms ok={ok}");
        if (!ok) return;

        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));
        var app = new CharacterAppearance { EnableEffect = true, Cape = new ItemInfo { Id = "1102149" } };
        foreach (var id in new[] { "30000", "31000" })
        {
            if (wz.FindNodeByPath($"Character/Hair/{id.PadLeft(8, '0')}.img") != null) { app.Hair = new ItemInfo { Id = id }; break; }
        }
        foreach (var id in new[] { "20000", "21000" })
        {
            if (wz.FindNodeByPath($"Character/Face/{id.PadLeft(8, '0')}.img") != null) { app.Face = new ItemInfo { Id = id }; break; }
        }

        // WZ 冷访问（body 首帧 ExtractPng）
        sw.Restart();
        wz.ExtractPng("Character/00002000.img/stand1/0/body");
        sw.Stop();
        Console.WriteLine($"WZ 冷访问 body 首帧: {sw.ElapsedMilliseconds}ms");

        // RenderFrame（SKBitmap 直绘路径，主窗口动画用）
        sw.Restart();
        var (bmp, _, _, _, _) = paperdoll.RenderFrame(app, "stand1", 0, "default");
        sw.Stop();
        Console.WriteLine($"首次 RenderFrame stand1/0: {sw.ElapsedMilliseconds}ms ({(bmp != null ? $"{bmp.Width}x{bmp.Height}" : "失败")})");
        bmp?.Dispose();
        // 第二个动作
        sw.Restart();
        var (bmp2, _, _, _, _) = paperdoll.RenderFrame(app, "walk1", 0, "default");
        sw.Stop();
        Console.WriteLine($"首次 RenderFrame walk1/0: {sw.ElapsedMilliseconds}ms");
        bmp2?.Dispose();
        // 缓存后动画帧
        sw.Restart();
        for (int f = 0; f < 30; f++) { var (b, _, _, _, _) = paperdoll.RenderFrame(app, "stand1", f, "default"); b?.Dispose(); }
        sw.Stop();
        Console.WriteLine($"缓存后 30 帧 RenderFrame: {sw.ElapsedMilliseconds}ms（{(sw.ElapsedMilliseconds / 30.0):F1}ms/帧）");
    }
}
