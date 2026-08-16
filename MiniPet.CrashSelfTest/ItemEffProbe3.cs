using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>特效结构细节：fixed 节点类型、effect/z 值、default/0 的 origin/z/map。</summary>
public static class ItemEffProbe3
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

        var root = "Effect/ItemEff.img/1102149/effect";
        // fixed 节点类型
        foreach (var key in new[] { "fixed", "z", "default", "ladder" })
        {
            var n = wz.FindNodeByPath($"{root}/{key}");
            Console.WriteLine($"  {key}: {(n != null ? n.Value?.GetType().Name ?? "null值" : "无")}");
            if (n != null && n.Value is Wz_Uol uol)
            {
                Console.WriteLine($"    uol 类型: {uol.GetType().Name}");
            }
        }
        // 顶层 z 值
        var zTop = wz.FindNodeByPath($"{root}/z")?.GetValueEx<int?>(null);
        Console.WriteLine($"  顶层 z = {(zTop.HasValue ? zTop.Value.ToString() : "无")}");
        // default/z
        var zDef = wz.FindNodeByPath($"{root}/default/z")?.GetValueEx<int?>(null);
        Console.WriteLine($"  default/z = {(zDef.HasValue ? zDef.Value.ToString() : "无")}");
        // default/0 的 origin/z/map
        var o = wz.GetOrigin($"{root}/default/0");
        var zf = wz.GetZField($"{root}/default/0");
        var m = wz.GetPieceMap($"{root}/default/0");
        Console.WriteLine($"  default/0 origin=({o.x},{o.y}) z=\"{zf}\" map={string.Join(";", m.Select(kv => $"{kv.Key}=({kv.Value.x},{kv.Value.y})"))}");
        // 帧数
        var def = wz.FindNodeByPath($"{root}/default");
        int frames = def?.Nodes.Count(n2 => int.TryParse(n2.Text, out _)) ?? 0;
        Console.WriteLine($"  default 数字帧数 = {frames}");
        // 有没有 stand1 动作帧？
        foreach (var act in new[] { "stand1", "walk1", "jump", "alert", "fixed" })
        {
            var a = wz.FindNodeByPath($"{root}/{act}");
            Console.WriteLine($"  {act}: {(a != null ? "存在" : "无")}");
        }
    }
}
