using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>Effect/ItemEff.img lazyload 全量加载耗时验证。</summary>
public static class ItemEffLoadProbe
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

        var sw = Stopwatch.StartNew();
        var node = wz.FindNodeByPath("Effect/ItemEff.img/1003953/effect");
        sw.Stop();
        Console.WriteLine($"首次 FindNodeByPath(ItemEff.img/1003953/effect): {sw.ElapsedMilliseconds}ms ({(node != null ? "存在" : "无")})");
        sw.Restart();
        var node2 = wz.FindNodeByPath("Effect/ItemEff.img/1102149/effect");
        sw.Stop();
        Console.WriteLine($"二次访问（同 img 不同 id）: {sw.ElapsedMilliseconds}ms");
        // 帧读取
        sw.Restart();
        var png = wz.ExtractPng("Effect/ItemEff.img/1003953/effect/default/0");
        sw.Stop();
        Console.WriteLine($"特效帧 ExtractPng: {sw.ElapsedMilliseconds}ms ({(png != null ? $"{png.Length}B" : "无")})");
    }
}
