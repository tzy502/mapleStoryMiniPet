using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>素材浏览器全量加载耗时（LoadFromWz：所有分类 + GetItemName 中文名）。</summary>
public static class BrowserLoadProbe
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

        // 全量加载（模拟素材浏览器构造 LoadFromWz）
        var sw = Stopwatch.StartNew();
        var entries = new List<(string Cat, string Id, string Name)>();
        // 发型（数量最大）
        foreach (var (id, _) in wz.GetDirectoryChildren("Character/Hair"))
        {
            var cn = wz.GetItemName("Hair", id);
            entries.Add(("发型", id, cn));
        }
        sw.Stop();
        Console.WriteLine($"发型 {entries.Count} 个: {sw.ElapsedMilliseconds}ms");
        // 脸型
        sw.Restart();
        var faces = 0;
        foreach (var (id, _) in wz.GetDirectoryChildren("Character/Face"))
        {
            wz.GetItemName("Face", id);
            faces++;
        }
        sw.Stop();
        Console.WriteLine($"脸型 {faces} 个: {sw.ElapsedMilliseconds}ms");
        // 帽子
        sw.Restart();
        var caps = 0;
        foreach (var (id, _) in wz.GetDirectoryChildren("Character/Cap"))
        {
            wz.GetItemName("Cap", id);
            caps++;
        }
        sw.Stop();
        Console.WriteLine($"帽子 {caps} 个: {sw.ElapsedMilliseconds}ms");
        // 其余装备
        sw.Restart();
        var other = 0;
        foreach (var folder in new[] { "Coat", "Longcoat", "Pants", "Shoes", "Glove", "Weapon", "Shield", "Cape", "Accessory", "TamingMob" })
        {
            foreach (var (id, _) in wz.GetDirectoryChildren($"Character/{folder}"))
            {
                wz.GetItemName(folder, id);
                other++;
            }
        }
        sw.Stop();
        Console.WriteLine($"其余装备 {other} 个: {sw.ElapsedMilliseconds}ms");
    }
}
