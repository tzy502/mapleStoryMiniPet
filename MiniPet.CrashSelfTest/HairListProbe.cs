using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>发型素材加载对比：WZ 实际发型 img 数 vs GetDirectoryChildren（素材浏览器加载路径）数。</summary>
public static class HairListProbe
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

        // 1) GetDirectoryChildren（浏览器路径）
        var listed = wz.GetDirectoryChildren("Character/Hair");
        Console.WriteLine($"GetDirectoryChildren(Character/Hair): {listed.Count} 个");

        // 2) 直接遍历目录节点（HairProbe 方式：扫全部 .img）
        var direct = new List<string>();
        var dirNode = wz.FindNodeByPath("Character/Hair");
        if (dirNode != null)
        {
            foreach (var c in dirNode.Nodes)
            {
                if (c.Text.EndsWith(".img"))
                {
                    direct.Add(c.Text);
                }
            }
        }
        Console.WriteLine($"直接遍历 Character/Hair: {direct.Count} 个 img");

        // 3) 差异
        var listedSet = new HashSet<string>(listed.Select(x => x.Id), StringComparer.Ordinal);
        var directIds = direct.Select(x => x[..^4]).ToList();
        var missing = directIds.Where(id => !listedSet.Contains(id)).ToList();
        Console.WriteLine($"直接有但 GetDirectoryChildren 缺: {missing.Count} 个");
        if (missing.Count > 0 && missing.Count <= 20)
        {
            Console.WriteLine($"  缺: {string.Join(",", missing)}");
        }
        else if (missing.Count > 0)
        {
            Console.WriteLine($"  前 20: {string.Join(",", missing.Take(20))}");
        }

        // 4) 其他分类对比（是否所有分类都缺）
        foreach (var folder in new[] { "Face", "Cap", "Cape", "Accessory", "Weapon" })
        {
            var l = wz.GetDirectoryChildren($"Character/{folder}");
            var d = new List<string>();
            var n = wz.FindNodeByPath($"Character/{folder}");
            if (n != null)
            {
                foreach (var c in n.Nodes)
                {
                    if (c.Text.EndsWith(".img")) { d.Add(c.Text); }
                }
            }
            Console.WriteLine($"  {folder}: GetDirectoryChildren={l.Count} 直接={d.Count}");
        }
    }
}
