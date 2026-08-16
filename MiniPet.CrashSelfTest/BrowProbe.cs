using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;

namespace MiniPet.CrashSelfTest;

/// <summary>brow 锚点探测：head/face/body 各部件 map 锚点表，确认特效锚点可用锚名。</summary>
public static class BrowProbe
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

        foreach (var (label, path) in new[]
        {
            ("body", "Character/00002000.img/stand1/0/body"),
            ("head", "Character/00012000.img/stand1/0/head"),
            ("hair", "Character/Hair/00030000.img/stand1/0/hair"),
            ("face", "Character/Face/00020000.img/blink/0/face"),
        })
        {
            var m = wz.GetPieceMap(path);
            if (m == null || m.Count == 0)
            {
                Console.WriteLine($"  {label}: 无 map");
                continue;
            }
            var parts = m.Select(kv => $"{kv.Key}=({kv.Value.x},{kv.Value.y})");
            Console.WriteLine($"  {label}: {string.Join(" ", parts)}");
        }
        // 特效精灵尺寸 + 特效帧 origin（default/0 与 default/1）
        foreach (var f in new[] { "0", "1", "5" })
        {
            var png = wz.ExtractPng($"Effect/ItemEff.img/1102149/effect/default/{f}");
            string dim = "?";
            if (png != null && png.Length > 24)
            {
                var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
                var h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
                dim = $"{w}x{h}";
            }
            var o = wz.GetOrigin($"Effect/ItemEff.img/1102149/effect/default/{f}");
            Console.WriteLine($"  特效 default/{f}: {dim} origin=({o.x},{o.y})");
        }
    }
}
