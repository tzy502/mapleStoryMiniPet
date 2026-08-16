using Avalonia;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MiniPet.Services;

namespace MiniPet;

class Program
{
    private const string AttemptArg = "--render-attempt";
    private const int MaxAttempts = 360;   // 每 10s 一次，约 1 小时上限，避免无限重启

    [STAThread]
    public static int Main(string[] args)
    {
        // 最早时机加载用户配置的 wzlib（先于任何 WzComparerR2 类型解析；失败回退内置 DLL）
        WzLibRuntimeLoader.TryLoadUserWzLib();

        int attempt = ParseAttempt(args);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex) when (IsRenderTimerError(ex))
        {
            return HandleRenderTimerFailure(args, attempt, ex);
        }
    }

    private static int ParseAttempt(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == AttemptArg && int.TryParse(args[i + 1], out var a)) return a;
        }
        return 0;
    }

    private static bool IsRenderTimerError(Exception ex)
        => ex is InvalidOperationException && ex.Message.Contains("RenderTimer");

    /// <summary>
    /// -6661: 渲染定时器拿不到（显示器休眠 / 进程后台化时 Avalonia.Native 无法注册）。
    /// 等待显示器恢复后重新拉起一个干净进程，避免在已半初始化的进程里重试（AvaloniaNative 静态残留）。
    /// </summary>
    private static int HandleRenderTimerFailure(string[] args, int attempt, Exception ex)
    {
        if (attempt >= MaxAttempts)
        {
            Console.Error.WriteLine($"[Program] 渲染定时器持续不可用，已重试 {attempt} 次，放弃: {ex.Message}");
            return 1;
        }

        Console.Error.WriteLine($"[Program] 渲染定时器不可用(显示器可能休眠)，10s 后重启进程(第 {attempt + 1}/{MaxAttempts} 次): {ex.Message}");
        Thread.Sleep(10000);

        try
        {
            // 转发原始参数（如 --mob），叠加新的重试计数
            var forwarded = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == AttemptArg) { i++; continue; }
                forwarded.Add(args[i]);
            }
            forwarded.Add(AttemptArg);
            forwarded.Add((attempt + 1).ToString());

            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? string.Empty,
                UseShellExecute = false
            };
            foreach (var a in forwarded) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception pex)
        {
            Console.Error.WriteLine($"[Program] 重启进程失败: {pex.Message}");
            return 1;
        }
        return 2;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
