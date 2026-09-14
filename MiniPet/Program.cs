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

        // 2026-08-17：日志落盘（Windows GUI 无控制台，Console 输出全部丢失——WZ 加载失败无迹可查）。
        // 写入 exe 根目录 MiniPet.log（不可写时回退 %USERPROFILE%/.minipet/minipet.log）。
        SetupFileLogging();

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

    /// <summary>
    /// 把 Console.Out/Error 重定向到日志文件（UTF-8 追加模式，AutoFlush）：
    /// 首选 exe 根目录 MiniPet.log，写入失败（只读目录等）回退用户配置目录。
    /// </summary>
    private static void SetupFileLogging()
    {
        try
        {
            string primary = System.IO.Path.Combine(AppContext.BaseDirectory, "MiniPet.log");
            string path = primary;
            try
            {
                using (var probe = new System.IO.StreamWriter(path, append: true) { AutoFlush = true })
                {
                    probe.WriteLine($"[MiniPet] 日志启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                }
            }
            catch
            {
                // exe 目录不可写 → 用户配置目录
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var dir = System.IO.Path.Combine(home, ".minipet");
                System.IO.Directory.CreateDirectory(dir);
                path = System.IO.Path.Combine(dir, "minipet.log");
            }
            var writer = new System.IO.StreamWriter(path, append: true)
            {
                AutoFlush = true
            };
            Console.SetOut(writer);
            Console.SetError(writer);
            Console.WriteLine($"[MiniPet] 日志文件: {path}");
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"[MiniPet] 日志初始化失败: {ex.Message}"); } catch { }
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
