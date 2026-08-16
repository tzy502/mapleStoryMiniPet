using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>坐骑名字探测：String.wz 里坐骑名字的真实位置 vs GetItemName("TamingMob")。</summary>
public static class MountNameProbe
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

        // 几个真实坐骑 id
        foreach (var id in new[] { "1902000", "1902001", "1930001", "1932294", "1920000" })
        {
            var n = wz.GetItemName("TamingMob", id);
            Console.WriteLine($"  TamingMob {id}: GetItemName(TamingMob)={n ?? "空"}");
        }
        // String/Eqp.img/Eqp 下 Taming 分类
        var eqp = wz.FindNodeByPath("String/Eqp.img/Eqp");
        if (eqp != null)
        {
            var taming = eqp.FindNodeByPath("Taming");
            Console.WriteLine($"  String/Eqp.img/Eqp/Taming 存在: {taming != null}");
            if (taming != null)
            {
                foreach (var id in new[] { "1902000", "1902001", "1930001" })
                {
                    var n = taming.FindNodeByPath($"{id}/name")?.GetValueEx<string>(null);
                    Console.WriteLine($"    Taming/{id}/name = {n ?? "无"}");
                }
            }
            // 也看其他可能分类
            foreach (var cat in new[] { "TamingMob", "RidingSkillInfo" })
            {
                var c2 = eqp.FindNodeByPath(cat);
                if (c2 != null)
                {
                    var n = c2.FindNodeByPath("1902000/name")?.GetValueEx<string>(null);
                    Console.WriteLine($"    Eqp/{cat}/1902000/name = {n ?? "无"}");
                }
            }
        }
        // Skill/RidingSkillInfo.img
        var riding = wz.FindNodeByPath("Skill/RidingSkillInfo.img");
        Console.WriteLine($"  Skill/RidingSkillInfo.img 存在: {riding != null}");
        if (riding != null)
        {
            foreach (var id in new[] { "1902000", "1902001", "1930001" })
            {
                var n = riding.FindNodeByPath($"{id}/name")?.GetValueEx<string>(null);
                Console.WriteLine($"    RidingSkillInfo/{id}/name = {n ?? "无"}");
            }
        }
    }
}
