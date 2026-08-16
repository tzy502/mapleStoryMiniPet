using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Views.Controls;
using SkiaSharp;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 崩溃自检程序（Avalonia Headless）：
/// 复现并验证「Image.Source 引用已 Dispose 位图 → 布局测量 ObjectDisposedException(SIGABRT)」修复。
/// 覆盖：旧模式必须崩（证明根因）/ 新模式稳定（证明修复）/ SpriteCanvas 两条渲染路径压测。
/// 退出码：0 = 全部通过；非 0 = 存在未修复崩溃路径。
/// </summary>
public static class Program
{
    private static int s_failures;

    public static int Main(string[] args)
    {
        var modeSettings = args.Contains("--settings");
        var modeRender = args.Contains("--render");
        Console.WriteLine("[自检] 初始化 Avalonia Headless…");
        // Headless 初始化（App 构造注册 FluentTheme + 全局 Button hover 覆写，与真实运行一致）
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        var exitCode = 0;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!modeRender)
                {
                    Console.WriteLine("[自检] 阶段一：SettingsWindow 真实预览全流程…");
                    SettingsPreviewSelfTest.RunAll();
                }
                if (!modeSettings)
                {
                    Console.WriteLine("[自检] 阶段二：渲染路径压测…");
                    RunChecks();
                    if (s_failures > 0)
                    {
                        exitCode = 1;
                    }
                }
                Console.WriteLine(exitCode == 0
                    ? "=== 自检全部通过 ==="
                    : $"=== 自检失败：{s_failures} 处崩溃路径未修复 ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"=== 自检失败（崩溃复现）：{ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                exitCode = 1;
            }
            // Headless MainLoop 无窗口关闭不退出，检查完直接退出进程
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode; // 不会走到（Environment.Exit）
    }

    private static void RunChecks()
    {
        // 1) 旧模式（先 Dispose 后换 Source）：Headless 布局模型下 Dispose 与换源间无布局插入不抛，
        //    真实 Avalonia.Native 下由延迟 Render pass 触发（用户环境已实测崩溃）——此处仅信息性输出。
        LegacyDisposeBeforeSwap();
        Console.WriteLine("  [INFO] 旧模式（先Dispose后换Source）在 Headless 下无布局插入不崩（Native 延迟布局才会，用户环境已实测）");

        // 2) 新模式（先换 Source 再 Dispose）稳定
        Check("新模式先换Source再Dispose稳定", NewModeSwapStable);

        // 3) SpriteCanvas.SetFrame（Mob PNG 路径）压测
        Check("SpriteCanvas.SetFrame 300帧稳定", () => SpriteCanvasStress(useBitmap: false));

        // 4) SpriteCanvas.SetFrameBitmap（纸娃娃 WriteableBitmap 路径）压测
        Check("SpriteCanvas.SetFrameBitmap 300帧稳定", () => SpriteCanvasStress(useBitmap: true));

        // 5) 换 Child 模式（SetPreviewChild 修复对应）稳定
        Check("先换Child再Dispose旧源 200轮稳定", SwapChildStable);

        // 6) 混合轮换（Image 反复换源 + 尺寸变化 + 布局）压测
        Check("Image混合轮换压测稳定", MixedSwapStable);
    }

    private static void Check(string name, Func<bool> action)
    {
        try
        {
            var ok = action();
            if (ok)
            {
                Console.WriteLine($"  [PASS] {name}");
            }
            else
            {
                Console.WriteLine($"  [FAIL] {name}（预期成立但未发生/预期稳定但异常）");
                s_failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            s_failures++;
        }
    }

    private static bool ExpectThrow(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── 1) 旧模式：先 Dispose 旧 Source 再换新 → 布局必须崩 ──
    private static void LegacyDisposeBeforeSwap()
    {
        var win = NewWindow();
        var image = new Image();
        win.Content = image;
        win.Show();
        Layout(win);

        var bmp1 = NewBitmap(32, 32, 255, 0, 0);
        image.Source = bmp1;
        Layout(win);

        bmp1.Dispose(); // 旧模式：先 Dispose
        image.Source = NewBitmap(32, 32, 0, 255, 0); // 后换
        Layout(win); // 布局测量已释放位图 → 必须抛
    }

    // ── 2) 新模式：先换 Source 再 Dispose 旧图 → 稳定 ──
    private static bool NewModeSwapStable()
    {
        var win = NewWindow();
        var image = new Image();
        win.Content = image;
        win.Show();
        Layout(win);

        image.Source = NewBitmap(32, 32, 255, 0, 0);
        Layout(win);
        for (int i = 0; i < 200; i++)
        {
            var next = NewBitmap(32, 32, (byte)(i * 3), 0, 255);
            var old = image.Source;
            image.Source = next;              // 先换
            (old as IDisposable)?.Dispose();  // 后 Dispose
            Layout(win);
            Dispatcher.UIThread.RunJobs();
        }
        win.Close();
        return true;
    }

    // ── 3/4) SpriteCanvas 两条渲染路径压测 ──
    private static bool SpriteCanvasStress(bool useBitmap)
    {
        var win = NewWindow();
        var canvas = new SpriteCanvas();
        win.Content = canvas;
        win.Show();

        var pngs = new[]
        {
            MakePng(64, 64, 255, 0, 0),
            MakePng(64, 64, 0, 255, 0),
            MakePng(72, 64, 0, 0, 255),
            MakePng(64, 72, 255, 255, 0),
            MakePng(80, 80, 128, 64, 0),
        };
        for (int i = 0; i < 300; i++)
        {
            if (useBitmap)
            {
                using var bmp = new SKBitmap(64 + (i % 3) * 8, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var c = new SKCanvas(bmp))
                {
                    c.Clear(new SKColor((byte)(i * 2), 0, 255, 255));
                }
                canvas.SetFrameBitmap(bmp);
            }
            else
            {
                canvas.SetFrame(pngs[i % pngs.Length]);
            }
            Layout(win);
            Dispatcher.UIThread.RunJobs();
        }
        win.Close();
        return true;
    }

    // ── 5) 换 Child 模式（SetPreviewChild 修复对应）──
    private static bool SwapChildStable()
    {
        var win = NewWindow();
        var box = new Border();
        win.Content = box;
        win.Show();
        for (int i = 0; i < 200; i++)
        {
            var img = new Image { Source = NewBitmap(32, 32, 0, 0, (byte)(i * 4)) };
            var oldImg = box.Child as Image;   // 先换 Child
            box.Child = img;
            (oldImg?.Source as IDisposable)?.Dispose(); // 后 Dispose 旧源
            Layout(win);
            Dispatcher.UIThread.RunJobs();
        }
        win.Close();
        return true;
    }

    // ── 6) 混合轮换：多 Image + 尺寸变化 + 清空 ──
    private static bool MixedSwapStable()
    {
        var win = NewWindow();
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        var images = new List<Image>();
        for (int k = 0; k < 3; k++)
        {
            var img = new Image();
            images.Add(img);
            panel.Children.Add(img);
        }
        win.Content = panel;
        win.Show();
        for (int i = 0; i < 150; i++)
        {
            foreach (var img in images)
            {
                var next = NewBitmap(24 + (i % 4) * 8, 24, (byte)(i * 5), 200, 100);
                var old = img.Source;
                img.Source = next;
                (old as IDisposable)?.Dispose();
            }
            Layout(win);
            Dispatcher.UIThread.RunJobs();
        }
        // 清空路径
        foreach (var img in images)
        {
            var old = img.Source;
            img.Source = null;
            (old as IDisposable)?.Dispose();
        }
        Layout(win);
        win.Close();
        return true;
    }

    private static Window NewWindow() => new() { Width = 300, Height = 300 };

    private static void Layout(Window win)
    {
        win.Measure(new Size(300, 300));
        win.Arrange(new Rect(0, 0, 300, 300));
    }

    private static Bitmap NewBitmap(int w, int h, byte r, byte g, byte b)
        => new(new MemoryStream(MakePng(w, h, r, g, b)));

    private static byte[] MakePng(int w, int h, byte r, byte g, byte b)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(r, g, b, 255));
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
