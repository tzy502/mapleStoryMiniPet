using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>数字 id 对比 GetDirectoryChildren vs 直接遍历（键格式修正）。</summary>
public static class HairListProbe2
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

        foreach (var folder in new[] { "Hair", "Face", "Cap", "Cape", "Accessory", "Weapon", "Mob", "Npc" })
        {
            var listed = wz.GetDirectoryChildren($"Character/{folder}");
            var listedSet = new HashSet<string>(listed.Select(x => x.Id), StringComparer.Ordinal);
            var d = new List<string>();
            var n = wz.FindNodeByPath($"Character/{folder}");
            if (n != null)
            {
                foreach (var c in n.Nodes)
                {
                    if (c.Text.EndsWith(".img"))
                    {
                        var k = c.Text[..^4];
                        if (int.TryParse(k, out var idNum)) { d.Add(idNum.ToString()); }
                    }
                }
            }
            var dSet = new HashSet<string>(d, StringComparer.Ordinal);
            var missing = dSet.Except(listedSet).ToList();
            var extra = listedSet.Except(dSet).ToList();
            Console.WriteLine($"  {folder}: listed={listed.Count} direct={dSet.Count} 缺={missing.Count} 多={extra.Count}");
            if (missing.Count > 0 && missing.Count <= 10) { Console.WriteLine($"    缺: {string.Join(",", missing)}"); }
            if (extra.Count > 0 && extra.Count <= 10) { Console.WriteLine($"    多: {string.Join(",", extra)}"); }
        }
    }
}
