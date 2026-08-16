using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>特效帧 ExtractPng 慢因分解：二次访问 / 普通部件对比 / PngEncoder 冷启动。</summary>
public static class ItemEffLoadProbe2
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

        // 普通部件首/二次
        var sw = Stopwatch.StartNew();
        var p1 = wz.ExtractPng("Character/00002000.img/stand1/0/body");
        sw.Stop();
        Console.WriteLine($"body 首次: {sw.ElapsedMilliseconds}ms ({(p1 != null ? p1.Length : 0)}B)");
        sw.Restart();
        wz.ExtractPng("Character/00002000.img/stand1/0/body");
        sw.Stop();
        Console.WriteLine($"body 二次: {sw.ElapsedMilliseconds}ms");
        // 特效帧首/二次
        sw.Restart();
        var e1 = wz.ExtractPng("Effect/ItemEff.img/1003953/effect/default/0");
        sw.Stop();
        Console.WriteLine($"特效帧首次: {sw.ElapsedMilliseconds}ms ({(e1 != null ? e1.Length : 0)}B)");
        sw.Restart();
        var e2 = wz.ExtractPng("Effect/ItemEff.img/1003953/effect/default/1");
        sw.Stop();
        Console.WriteLine($"特效帧二次(帧1): {sw.ElapsedMilliseconds}ms ({(e2 != null ? e2.Length : 0)}B)");
        // Wz_Png 头读取（不 Encode）
        sw.Restart();
        var sz = wz.GetPngSize("Effect/ItemEff.img/1003953/effect/default/0");
        sw.Stop();
        Console.WriteLine($"特效帧 GetPngSize: {sw.ElapsedMilliseconds}ms ({(sz.HasValue ? $"{sz.Value.W}x{sz.Value.H}" : "无")})");
    }
}
