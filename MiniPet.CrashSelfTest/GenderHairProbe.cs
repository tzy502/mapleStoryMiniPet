using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace MiniPet.CrashSelfTest;

/// <summary>性别过滤规则验证（MapleSalon2 getHairGender/getFaceGender 千位规则复算）。</summary>
public static class GenderHairProbe
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

    private static int HairGender(int n)
    {
        int tag = (n / 1000) % 10;
        return tag switch { 0 or 3 or 5 or 6 => 0, 1 or 4 or 7 or 8 => 1, _ => 2 };
    }

    private static int FaceGender(int n)
    {
        int tag = (n / 1000) % 10;
        return tag switch { 0 or 3 or 5 or 7 => 0, 1 or 4 or 6 or 8 => 1, _ => 2 };
    }

    private static void Run()
    {
        foreach (var id in new[] { "36633", "30003", "45383", "61423", "35590", "34000", "37000" })
        {
            var n = int.Parse(id);
            int g = HairGender(n);
            Console.WriteLine($"  发型 {id}: 千位={(n / 1000) % 10} → {(g == 2 ? "通用" : g == 0 ? "男" : "女")}  男可见={g == 2 || g == 0} 女可见={g == 2 || g == 1}");
        }
        foreach (var id in new[] { "20000", "21000", "26000", "28000", "23000" })
        {
            var n = int.Parse(id);
            int g = FaceGender(n);
            Console.WriteLine($"  脸型 {id}: 千位={(n / 1000) % 10} → {(g == 2 ? "通用" : g == 0 ? "男" : "女")}  男可见={g == 2 || g == 0} 女可见={g == 2 || g == 1}");
        }
    }
}
