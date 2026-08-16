using System;
using System.Runtime.InteropServices;

namespace MiniPet.Utils
{
    /// <summary>
    /// Windows 原生窗口辅助（user32 P/Invoke，不引入任何 NuGet 依赖）。
    /// 用途：
    ///   1. 点击穿透——桌宠/背景/气泡是透明顶层窗口，Windows 上默认仍会拦截鼠标点击，
    ///      需置 WS_EX_TRANSPARENT 扩展样式才能让点击穿透到下层窗口（等价 macOS
    ///      NSWindow.ignoresMouseEvents）。透明/分层样式由 Avalonia 自身维护，这里只增删
    ///      WS_EX_TRANSPARENT，不动其他位。
    ///   2. 全局鼠标坐标——GetCursorPos（物理像素，屏幕坐标系），交互模式手动拖动用。
    /// 平台限定：仅 Windows 调用（调用方已按 OperatingSystem.IsWindows() 分流）；
    /// P/Invoke 入口 win-x64 目标（验收发布目标 self-contained win-x64）。
    /// </summary>
    public static class WindowsNative
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TRANSPARENT = 0x00000020L;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        /// <summary>设置/清除窗口点击穿透（WS_EX_TRANSPARENT）。成功返回 true；非 Windows/句柄无效/调用失败返回 false。</summary>
        public static bool SetClickThrough(IntPtr hwnd, bool enabled)
        {
            if (hwnd == IntPtr.Zero) { return false; }
            if (!OperatingSystem.IsWindows()) { return false; }
            try
            {
                long style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
                long newStyle = enabled ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
                SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(newStyle));
                return true;
            }
            catch
            {
                // 调用失败不致命：最坏结果 = 窗口仍拦截点击（macOS 上旧实现同样 catch 静默）
                return false;
            }
        }

        /// <summary>全局鼠标位置（物理像素，屏幕坐标）；非 Windows/失败返回 null。</summary>
        public static (int X, int Y)? GetCursorPosition()
        {
            if (!OperatingSystem.IsWindows()) { return null; }
            try
            {
                if (GetCursorPos(out var p)) { return (p.X, p.Y); }
            }
            catch
            {
                // 忽略，调用方回退
            }
            return null;
        }
    }
}
