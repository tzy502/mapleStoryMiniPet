using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>冰凌披风特效验证：穿 1102149 合成多帧（特效动画 18 帧循环），对比 EnableEffect 开关前后输出字节差异。</summary>
public static class IceCapeEffectProbe
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

        // 外观：默认身体 + 发型/脸型探测（WZ 缺则跳过）+ 冰凌披风
        var app = new CharacterAppearance();
        app.EnableEffect = true;
        app.Cape = new ItemInfo { Id = "1102149" };
        // 基础发型/脸型（探测存在的 id）
        foreach (var id in new[] { "30000", "31000" })
        {
            if (wz.FindNodeByPath($"Character/Hair/{id.PadLeft(8, '0')}.img") != null) { app.Hair = new ItemInfo { Id = id }; break; }
        }
        foreach (var id in new[] { "20000", "21000" })
        {
            if (wz.FindNodeByPath($"Character/Face/{id.PadLeft(8, '0')}.img") != null) { app.Face = new ItemInfo { Id = id }; break; }
        }

        for (int frame = 0; frame < 6; frame++)
        {
            var withFx = paperdoll.CompositeFrame(app, "stand1", frame);
            app.EnableEffect = false;
            var withoutFx = paperdoll.CompositeFrame(app, "stand1", frame);
            app.EnableEffect = true;
            int fxSize = withFx?.Length ?? 0;
            int noSize = withoutFx?.Length ?? 0;
            Console.WriteLine($"帧 {frame}: 特效开={fxSize}B 特效关={noSize}B 差={fxSize - noSize}B {(fxSize > noSize ? "✓ 特效已渲染" : "✗ 无差异")}");
            if (frame == 0 && withFx != null)
            {
                System.IO.File.WriteAllBytes("/tmp/icecape_fx.png", withFx);
                System.IO.File.WriteAllBytes("/tmp/icecape_nofx.png", withoutFx ?? Array.Empty<byte>());
            }
        }
    }
}
