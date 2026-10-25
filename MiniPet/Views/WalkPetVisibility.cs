using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MiniPet.Views;

/// <summary>
/// 行走模式的多宠显示控制（F18 PLAN §1「多宠：先默认只有主宠参与行走，其他桌宠全部隐藏」，
/// §7 ⑤ 默认值 = 仅主宠，其余 <c>Hide()</c>）。
/// <para>
/// 集合口径对齐 <c>PetManager</c>：它持有 <c>IReadOnlyList&lt;PetWindow&gt;</c>（<see cref="MiniPet.Views.PetWindow"/> : Window），
/// 经 <c>IEnumerable&lt;T&gt;</c> 协变可直接传给 <see cref="IEnumerable{Window}"/> 形参，调用侧无需转换。
/// </para>
/// <para>
/// 隐藏用 <c>Window.Hide()</c>（对齐 <c>TrayService</c> 的显隐口径：Hide/Show 会改 IsVisible，
/// PetWindow 据此停/启帧循环与气泡定时器）；恢复按「进入前的可见性」还原，句柄 Dispose 幂等。
/// </para>
/// </summary>
public static class WalkPetVisibility
{
    /// <summary>
    /// 进入行走模式的显示收敛：只保留 <paramref name="primaryPet"/> 可见，其余窗口全部 <c>Hide()</c>。
    /// 主宠若原本被隐藏（例如隐藏到托盘），会被 <c>Show()</c> 出来并在恢复时重新隐藏。
    /// 返回的句柄 Dispose 即还原（幂等，可重复 Dispose）。
    /// </summary>
    public static IDisposable HideOthers(Window? primaryPet, IEnumerable<Window>? allPets)
    {
        var handle = new RestoreHandle();
        RunOnUI(() => handle.Apply(primaryPet, allPets));
        return handle;
    }

    /// <summary>
    /// 保证在 UI 线程执行（访存 IsVisible / Hide / Show 都必须在 UI 线程）。
    /// 取不到 UI 调度器（无头 / 单测环境）时退化为当前线程直接执行，保证语义不静默丢失。
    /// </summary>
    private static void RunOnUI(Action action)
    {
        bool needMarshal = false;
        try { needMarshal = !Dispatcher.UIThread.CheckAccess(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WalkPetVisibility] CheckAccess: {ex.Message}");
            needMarshal = false;
        }
        if (!needMarshal)
        {
            SafeInvoke(action);
            return;
        }
        try { Dispatcher.UIThread.Invoke(action); }
        catch (Exception ex)
        {
            // Invoke 本身失败（调度器未运行）才走这里；action 内部已全量 try/catch，不会因 action 抛异常而重复执行
            Console.Error.WriteLine($"[WalkPetVisibility] Invoke: {ex.Message}");
            SafeInvoke(action);
        }
    }

    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WalkPetVisibility] {ex.Message}"); }
    }

    /// <summary>
    /// 恢复句柄：只记录「本次真正改动过」的窗口，Dispose 时按原状态还原。
    /// 嵌套/重复 HideOthers 时，后一个句柄看到的已隐藏窗口不记账，
    /// 因此先释放哪个都不会把窗口提前显示出来。
    /// </summary>
    private sealed class RestoreHandle : IDisposable
    {
        private readonly object _gate = new();
        /// <summary>本来可见、被本次 Hide() 掉的非主宠窗口。</summary>
        private readonly List<Window> _hiddenByUs = new();
        /// <summary>本来隐藏、被本次 Show() 出来的主宠（恢复时重新 Hide()）。</summary>
        private Window? _primaryForcedShown;
        private bool _applied;
        private bool _disposed;

        internal void Apply(Window? primary, IEnumerable<Window>? allPets)
        {
            lock (_gate)
            {
                if (_applied || _disposed) { return; }
                _applied = true;
                try
                {
                    var seen = new HashSet<Window>();
                    if (allPets != null)
                    {
                        foreach (var win in allPets)
                        {
                            if (win == null) { continue; }
                            if (ReferenceEquals(win, primary)) { continue; }
                            if (!seen.Add(win)) { continue; }
                            try
                            {
                                // 只处理「当前可见」的：已经隐藏的窗口不动也不记账，避免恢复时把托盘隐藏的宠物弹出来
                                if (win.IsVisible)
                                {
                                    win.Hide();
                                    _hiddenByUs.Add(win);
                                }
                            }
                            catch (Exception ex) { Console.Error.WriteLine($"[WalkPetVisibility] Hide({Describe(win)}): {ex.Message}"); }
                        }
                    }
                    if (primary != null)
                    {
                        try
                        {
                            if (!primary.IsVisible)
                            {
                                primary.Show();
                                _primaryForcedShown = primary;
                            }
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[WalkPetVisibility] ShowPrimary: {ex.Message}"); }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[WalkPetVisibility] Apply: {ex.Message}"); }
                Console.WriteLine($"[WalkPetVisibility] 行走模式：隐藏 {_hiddenByUs.Count} 只非主宠" +
                                  (_primaryForcedShown != null ? "（主宠原为隐藏，已临时显示）" : string.Empty));
            }
        }

        public void Dispose()
        {
            RunOnUI(Restore);
        }

        private void Restore()
        {
            lock (_gate)
            {
                if (_disposed) { return; }
                _disposed = true;
                foreach (var win in _hiddenByUs)
                {
                    try
                    {
                        // 期间被用户/其他地方重新显示的，不再重复 Show
                        if (!win.IsVisible) { win.Show(); }
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[WalkPetVisibility] RestoreShow({Describe(win)}): {ex.Message}"); }
                }
                _hiddenByUs.Clear();
                if (_primaryForcedShown != null)
                {
                    try
                    {
                        if (_primaryForcedShown.IsVisible) { _primaryForcedShown.Hide(); }
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[WalkPetVisibility] RestoreHidePrimary: {ex.Message}"); }
                    _primaryForcedShown = null;
                }
            }
        }

        /// <summary>日志用短标识；不用反射，只取窗口标题，取不到就退回类型名。</summary>
        private static string Describe(Window win)
        {
            try { return string.IsNullOrEmpty(win.Title) ? win.GetType().Name : win.Title!; }
            catch { return win.GetType().Name; }
        }
    }
}
