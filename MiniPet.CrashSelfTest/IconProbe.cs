using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using MiniPet.Views;

namespace MiniPet.CrashSelfTest;

/// <summary>icon 数据探查：验证真实 WZ 装备节点 icon/iconRaw 字段是否存在（--probe-icon 模式）。</summary>
public static class IconProbe
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
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, wzConfig.BaseWzPath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) return;

        // 各类别取一个真实 id 探测 icon/iconRaw
        var probes = new (string Cat, string Id, string Root)[]
        {
            ("帽子", "1000000", "Character/Cap/01000000.img"),
            ("上衣", "1040000", "Character/Coat/01040000.img"),
            ("裤子", "1060000", "Character/Pants/01060000.img"),
            ("发型", "30000", "Character/Hair/00030000.img"),
            ("脸型", "20000", "Character/Face/00020000.img"),
            ("鞋子", "1070000", "Character/Shoes/01070000.img"),
            ("手套", "1080000", "Character/Glove/01080000.img"),
            ("武器", "1302000", "Character/Weapon/01302000.img"),
        };
        foreach (var (cat, id, root) in probes)
        {
            var node = wz.FindNodeByPath(root);
            if (node == null)
            {
                Console.WriteLine($"[{cat}/{id}] {root} → 节点不存在");
                continue;
            }
            // MapleSalon2 getIconPath：装备 icon 在 info/icon；Face=blink/0/face；Hair=default/hair
            var iconP = cat == "脸型" ? "blink/0/face" : cat == "发型" ? "default/hair" : "info/icon";
            var iconNode = wz.FindNodeByPath($"{root}/{iconP}");
            var iconPng = iconNode != null ? wz.ExtractPng($"{root}/{iconP}") : null;
            Console.WriteLine($"[{cat}/{id}] {iconP} 字段={(iconNode != null ? "有" : "无")} ExtractPng={(iconPng != null && iconPng.Length > 0 ? $"{iconPng.Length}B" : "失败")}");
        }
    }
}
