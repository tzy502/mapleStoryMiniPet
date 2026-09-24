using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MiniPet.Views;

/// <summary>
/// 行走模式的地图切换过渡（F18 PLAN §7 ③：0.5s = 250ms 淡出 + 250ms 淡入）。
/// 对齐项目既有实现 <c>BalloonControl.FadeTo</c>（opacity + Task.Delay 帧循环），
/// 区别：① 作用对象是窗口整体（Window.Opacity，对齐 PLAN §8 的 MapTransition「窗口 opacity 动画」）；
/// ② 改为 Stopwatch 计时，保证总时长与入参一致（固定步数会因 Task.Delay 精度漂移）；
/// ③ 同一窗口的重复过渡用 CancellationTokenSource 取消旧的一次（新过渡接管 opacity）。
/// 所有异常就地吞掉并打日志：过渡失败最坏只是窗口停在某个透明度，绝不冒泡崩窗口。
/// </summary>
public static class WalkTransition
{
    /// <summary>默认过渡总时长（ms）：250 淡出 + 250 淡入。</summary>
    public const int DefaultMs = 500;

    /// <summary>帧间隔（ms），与 <c>BalloonControl.FadeTo</c> 的 16ms/帧一致（约 60fps）。</summary>
    private const int FrameMs = 16;

    /// <summary>透明度差小于该值视为到位，直接落目标值（避免无意义的 1 帧循环）。</summary>
    private const double Epsilon = 0.0005;

    /// <summary>
    /// 每个窗口最多一个进行中的过渡。用条件弱表：窗口被回收时表项自动消失，不会因静态字段泄漏窗口。
    /// Begin 时先取消并摘除旧条目——旧过渡的 finally 用 <c>ReferenceEquals</c> 判断自己是否已被接管，不会误删新条目。
    /// </summary>
    private static readonly ConditionalWeakTable<Window, CancellationTokenSource> _active = new();

    /// <summary>保护 _active 的「取出旧条目 / 写新条目」复合操作（条件弱表本身线程安全，但复合操作不是原子的）。</summary>
    private static readonly object _gate = new();

    /// <summary>
    /// 对窗口做整体淡出 → <paramref name="swap"/>（切图）→ 淡入。返回的 Task 可 await。
    /// swap 抛异常也会继续淡入（不能让窗口留在全透明态）；被新过渡取消时不再淡入（交给新过渡收尾）。
    /// </summary>
    public static async Task CrossFadeAsync(Window window, Action? swap, int totalMs = DefaultMs)
    {
        if (window == null) { return; }
        int total = totalMs > 0 ? totalMs : DefaultMs;
        int half = Math.Max(1, total / 2);
        var cts = Begin(window);
        try
        {
            await FadeToAsync(window, 0.0, half, cts.Token).ConfigureAwait(true);
            if (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    // 切图要碰窗口/地图资源，同样保证 UI 线程（DispatcherOperation 自带 awaiter，不能接 ConfigureAwait）
                    if (Dispatcher.UIThread.CheckAccess()) { swap?.Invoke(); }
                    else { await Dispatcher.UIThread.InvokeAsync(() => swap?.Invoke()); }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] swap: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] CrossFade(淡出): {ex.Message}"); }
        finally
        {
            // 只有「没被新过渡接管」时才淡回：否则会和新过渡抢 opacity
            if (!cts.Token.IsCancellationRequested)
            {
                try { await FadeToAsync(window, 1.0, half, cts.Token).ConfigureAwait(true); }
                catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] CrossFade(淡入): {ex.Message}"); }
            }
            End(window, cts);
        }
    }

    /// <summary>淡出到全透明（opacity = 0）。重复调用会取消同一窗口上一次过渡。</summary>
    public static async Task FadeOutAsync(Window window, int ms)
    {
        if (window == null) { return; }
        var cts = Begin(window);
        try { await FadeToAsync(window, 0.0, ms, cts.Token).ConfigureAwait(true); }
        catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] FadeOut: {ex.Message}"); }
        finally { End(window, cts); }
    }

    /// <summary>淡入到不透明（opacity = 1）。重复调用会取消同一窗口上一次过渡。</summary>
    public static async Task FadeInAsync(Window window, int ms)
    {
        if (window == null) { return; }
        var cts = Begin(window);
        try { await FadeToAsync(window, 1.0, ms, cts.Token).ConfigureAwait(true); }
        catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] FadeIn: {ex.Message}"); }
        finally { End(window, cts); }
    }

    /// <summary>
    /// 线性把窗口 opacity 推到 <paramref name="target"/>，耗时约 <paramref name="ms"/>。
    /// 必须在 UI 线程改 opacity：非 UI 线程调用者先经 Dispatcher 切回（Avalonia 的 UI 同步上下文
    /// 保证内部 await 续体仍在 UI 线程），否则跨线程改 Visual 属性会抛。
    /// </summary>
    private static async Task FadeToAsync(Window window, double target, int ms, CancellationToken ct)
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                // 用一个显式 Func<Task> 变量，避免 Action / Func<Task> 两个重载的歧义
                Func<Task> op = () => FadeToAsync(window, target, ms, ct);
                await Dispatcher.UIThread.InvokeAsync(op).ConfigureAwait(false);
                return;
            }

            double start = window.Opacity;
            // macOS 合成树下 Window.Opacity 动画观感弱（胶水 2026-09-16「切换不自然 淡入淡出需要」）：
            // 同步作用于内容根（Panel），窗口+内容双层透明度变化在 mac 上肉眼可见。
            var content = window.Content as Avalonia.Controls.Panel;
            double startC = content?.Opacity ?? 1.0;
            double diff = target - start;
            if (ms <= 0 || Math.Abs(diff) < Epsilon)
            {
                window.Opacity = target;
                if (content != null) { content.Opacity = target; }
                return;
            }

            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (ct.IsCancellationRequested) { return; }
                double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / ms);
                try
                {
                    window.Opacity = start + diff * t;
                    if (content != null) { content.Opacity = startC + (target - startC) * t; }
                }
                catch (Exception ex)
                {
                    // 窗口正在关闭/已销毁：保持当前值退出，不冒泡
                    Console.Error.WriteLine($"[WalkTransition] Opacity: {ex.Message}");
                    return;
                }
                if (t >= 1.0) { return; }
                try { await Task.Delay(FrameMs, ct).ConfigureAwait(true); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] FadeTo: {ex.Message}"); }
    }

    /// <summary>登记本次过渡并取消同一窗口上一次过渡（旧 CTS 由旧过渡自己的 finally 释放）。</summary>
    private static CancellationTokenSource Begin(Window window)
    {
        var cts = new CancellationTokenSource();
        CancellationTokenSource? old = null;
        lock (_gate)
        {
            try
            {
                if (_active.TryGetValue(window, out var prev))
                {
                    _active.Remove(window);
                    old = prev;
                }
                _active.Add(window, cts);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] Begin: {ex.Message}"); }
        }
        // 在锁外取消：Cancel() 会同步跑续体（Task.Delay 的取消回调），避免持锁期间回调重入
        if (old != null)
        {
            try { old.Cancel(); }
            catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] Cancel: {ex.Message}"); }
        }
        return cts;
    }

    /// <summary>收尾：只有自己仍是「当前过渡」时才摘表（被新过渡接管时不能误删新条目），然后释放自己。</summary>
    private static void End(Window window, CancellationTokenSource cts)
    {
        lock (_gate)
        {
            try
            {
                if (_active.TryGetValue(window, out var cur) && ReferenceEquals(cur, cts))
                {
                    _active.Remove(window);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] End: {ex.Message}"); }
        }
        try { cts.Dispose(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WalkTransition] Dispose: {ex.Message}"); }
    }
}
