using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>神之子头冠：定位 id + 渲染耗时分解。</summary>
public static class SonGodCapProbe
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

        // 1) 搜「神之子」装备名
        Console.WriteLine("=== 搜「神之子」装备 ===");
        string? capId = null;
        var capCat = wz.FindNodeByPath("String/Eqp.img/Eqp/Cap");
        if (capCat != null)
        {
            var root = capCat;
            var img = capCat.GetValue<Wz_Image>();
            if (img != null && img.TryExtract()) { root = img.Node; }
            foreach (var idNode in root.Nodes)
            {
                var name = idNode.FindNodeByPath("name")?.GetValueEx<string>(null);
                if (name != null && name.Contains("神之子"))
                {
                    Console.WriteLine($"  [{idNode.Text}] → {name}");
                    if (name.Contains("头冠") && capId == null) { capId = idNode.Text; }
                }
            }
        }
        if (capId == null)
        {
            Console.WriteLine("  未找到「神之子」头冠——退出");
            return;
        }

        // 2) 特效存在性
        var eff = wz.FindNodeByPath($"Effect/ItemEff.img/{capId}/effect");
        Console.WriteLine($"  {capId} Effect/ItemEff.img/effect: {(eff != null ? "存在" : "无")}");
        // Cap img 帧结构
        var capImg = wz.FindNodeByPath($"Character/Cap/{capId.PadLeft(8, '0')}.img");
        var s0 = capImg?.FindNodeByPath("stand1/0");
        if (s0 != null)
        {
            foreach (var c in s0.Nodes)
            {
                var png = c.GetValue<Wz_Png>();
                Console.WriteLine($"    stand1/0/{c.Text}: {(png != null ? $"{png.Width}x{png.Height}" : c.Value?.GetType().Name)}");
            }
        }

        // 3) 渲染耗时
        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));
        var app = new CharacterAppearance { EnableEffect = true, Cap = new ItemInfo { Id = capId } };
        foreach (var id in new[] { "30000", "31000" })
        {
            if (wz.FindNodeByPath($"Character/Hair/{id.PadLeft(8, '0')}.img") != null) { app.Hair = new ItemInfo { Id = id }; break; }
        }
        foreach (var id in new[] { "20000", "21000" })
        {
            if (wz.FindNodeByPath($"Character/Face/{id.PadLeft(8, '0')}.img") != null) { app.Face = new ItemInfo { Id = id }; break; }
        }
        var sw = Stopwatch.StartNew();
        var png1 = paperdoll.CompositeFrame(app, "stand1", 0);
        sw.Stop();
        Console.WriteLine($"首次 CompositeFrame stand1/0: {sw.ElapsedMilliseconds}ms ({(png1 != null ? $"{png1.Length}B" : "失败")})");
        sw.Restart();
        paperdoll.CompositeFrame(app, "walk1", 0);
        sw.Stop();
        Console.WriteLine($"首次 walk1/0: {sw.ElapsedMilliseconds}ms");
        sw.Restart();
        for (int f = 0; f < 10; f++) { paperdoll.CompositeFrame(app, "stand1", f); }
        sw.Stop();
        Console.WriteLine($"缓存后 10 帧: {sw.ElapsedMilliseconds}ms");
        // RenderFrame（主窗口路径）
        sw.Restart();
        var (bmp, _, _, _, _) = paperdoll.RenderFrame(app, "stand1", 0, "default");
        sw.Stop();
        Console.WriteLine($"RenderFrame stand1/0（暖后）: {sw.ElapsedMilliseconds}ms ({(bmp != null ? $"{bmp.Width}x{bmp.Height}" : "失败")})");
        bmp?.Dispose();
    }
}
