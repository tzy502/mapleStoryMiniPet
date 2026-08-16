using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>完整「黄」与「电」发型扫描：String.wz Hair 全量匹配。</summary>
public static class YellowHairProbe2
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

        var hairCat = wz.FindNodeByPath("String/Eqp.img/Eqp/Hair");
        if (hairCat == null) { Console.WriteLine("Hair 分类不存在"); return; }
        var root = hairCat;
        var img = hairCat.GetValue<Wz_Image>();
        if (img != null && img.TryExtract()) { root = img.Node; }

        int yellow = 0, dian = 0, yellowDian = 0, total = 0;
        var yellowDianList = new List<string>();
        foreach (var idNode in root.Nodes)
        {
            total++;
            var name = idNode.FindNodeByPath("name")?.GetValueEx<string>(null);
            if (name == null) { continue; }
            if (name.Contains("黄")) { yellow++; }
            if (name.Contains("电")) { dian++; }
            if (name.StartsWith("黄色电") || name.Contains("黄") && name.Contains("电"))
            {
                yellowDian++;
                yellowDianList.Add($"{idNode.Text}={name}");
            }
        }
        Console.WriteLine($"String Hair 分类总数: {total}，含「黄」: {yellow}，含「电」: {dian}，黄+电: {yellowDian}");
        foreach (var s in yellowDianList) { Console.WriteLine($"  {s}"); }
        // 含「电」的完整列表
        Console.WriteLine("=== 含「电」的发型 ===");
        foreach (var idNode in root.Nodes)
        {
            var name = idNode.FindNodeByPath("name")?.GetValueEx<string>(null);
            if (name != null && name.Contains("电"))
            {
                Console.WriteLine($"  {idNode.Text} → {name}");
            }
        }
    }
}
