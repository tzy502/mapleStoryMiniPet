using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>冰凌披风加载耗时验证：首次合成（含 GetBounds 全动作边界计算）与后续帧耗时。</summary>
public static class IceCapePerfProbe
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
        var app = new CharacterAppearance { EnableEffect = true, Cape = new ItemInfo { Id = "1102149" } };
        foreach (var id in new[] { "30000", "31000" })
        {
            if (wz.FindNodeByPath($"Character/Hair/{id.PadLeft(8, '0')}.img") != null) { app.Hair = new ItemInfo { Id = id }; break; }
        }
        foreach (var id in new[] { "20000", "21000" })
        {
            if (wz.FindNodeByPath($"Character/Face/{id.PadLeft(8, '0')}.img") != null) { app.Face = new ItemInfo { Id = id }; break; }
        }

        // 首次合成（含 bounds 计算）计时
        var sw = Stopwatch.StartNew();
        var png = paperdoll.CompositeFrame(app, "stand1", 0);
        sw.Stop();
        Console.WriteLine($"首次合成 stand1/0: {sw.ElapsedMilliseconds}ms ({(png != null ? $"{png.Length}B" : "失败")})");
        // 切动作（walk1 首次 = 新 bounds）
        sw.Restart();
        paperdoll.CompositeFrame(app, "walk1", 0);
        sw.Stop();
        Console.WriteLine($"首次 walk1/0: {sw.ElapsedMilliseconds}ms");
        // 后续帧（缓存命中）
        sw.Restart();
        for (int f = 0; f < 18; f++) { paperdoll.CompositeFrame(app, "stand1", f); }
        sw.Stop();
        Console.WriteLine($"stand1 后续 18 帧: {sw.ElapsedMilliseconds}ms（含 PNG 编码）");
    }
}
