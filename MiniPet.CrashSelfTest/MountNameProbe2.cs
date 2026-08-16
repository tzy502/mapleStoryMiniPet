using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>坐骑名字：Taming 分类节点结构实测（id 格式/name 位置）。</summary>
public static class MountNameProbe2
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

        var eqp = wz.FindNodeByPath("String/Eqp.img");
        var root = eqp;
        var img = eqp?.GetValue<Wz_Image>();
        if (img != null && img.TryExtract()) { root = img.Node; }
        var taming = root?.FindNodeByPath("Eqp/Taming");
        if (taming == null) { Console.WriteLine("Taming 分类不存在"); return; }
        Console.WriteLine($"Taming 节点数: {taming.Nodes.Count}");
        var shown = 0;
        foreach (var n in taming.Nodes)
        {
            if (shown++ >= 12) { break; }
            var nm = n.FindNodeByPath("name")?.GetValueEx<string>(null);
            Console.WriteLine($"  {n.Text}: name={(nm ?? "无")}");
        }
        // 找 1902000 的多种写法
        foreach (var key in new[] { "1902000", "01902000", "1902000.img" })
        {
            var node = taming.FindNodeByPath(key);
            if (node != null)
            {
                var nm = node.FindNodeByPath("name")?.GetValueEx<string>(null);
                Console.WriteLine($"  Taming/{key}: 存在 name={(nm ?? "无")}");
            }
        }
        // RidingSkillInfo 结构
        var riding = wz.FindNodeByPath("Skill/RidingSkillInfo.img");
        var rRoot = riding;
        var rImg = riding?.GetValue<Wz_Image>();
        if (rImg != null && rImg.TryExtract()) { rRoot = rImg.Node; }
        Console.WriteLine($"RidingSkillInfo 节点数: {rRoot?.Nodes.Count ?? 0}");
        var rs = 0;
        if (rRoot != null)
        {
            foreach (var n in rRoot.Nodes)
            {
                if (rs++ >= 8) { break; }
                Console.WriteLine($"  {n.Text}");
            }
        }
    }
}
