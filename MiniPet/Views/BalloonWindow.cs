using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MiniPet.Utils;
using MiniPet.Views.Controls;

namespace MiniPet.Views
{
    /// <summary>
    /// 聊天气泡独立窗口（胶水规则 2026-08-08）：
    /// 气泡在角色头顶 = 主窗口（角色帧）上方区域，macOS 系统限制窗口顶部不能超过菜单栏
    /// （Position.Y ≥ ~30，攻击帧补偿需窗口顶出屏被 clamp → 角色跳 + 拖不到顶）。
    /// 独立窗口方案：主窗口 = 角色帧（无 headRoom，可拖到顶），气泡窗口独立跟随固定锚点，
    /// 动画切换时气泡位置不动（锚点基准 = 拖拽结束时的角色锚点）。
    /// 默认点击穿透（NSWindow.ignoresMouseEvents，同 BackgroundWindow ADR-0003）。
    /// </summary>
    public class BalloonWindow : Window
    {
        private readonly BalloonControl _balloonControl = new BalloonControl { ClipToBounds = false };
        private bool _passthroughApplied;

        public BalloonControl BalloonControl => _balloonControl;

        public BalloonWindow()
        {
            Width = 1;
            Height = 1;
            CanResize = false;
            ShowInTaskbar = false;
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = true; // 气泡在桌宠之上
            IsHitTestVisible = false;

            var canvas = new Canvas { ClipToBounds = false };
            canvas.Children.Add(_balloonControl);
            Content = canvas;
        }

        public void ShowBalloonWindow()
        {
            ApplyPassthrough();
            Show();
        }

        public void HideBalloonWindow()
        {
            Hide();
        }

        /// <summary>确保点击穿透（macOS 原生 NSWindow.ignoresMouseEvents / Windows WS_EX_TRANSPARENT）。</summary>
        private void ApplyPassthrough()
        {
            if (_passthroughApplied) return;
            try
            {
                var handle = TryGetPlatformHandle()?.Handle;
                if (handle is nint h && h != 0)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        // Windows：WS_EX_TRANSPARENT 点击穿透（失败最坏 = 气泡拦截点击，不抛）
                        WindowsNative.SetClickThrough(h, true);
                        _passthroughApplied = true;
                        return;
                    }
                    objc_msgSend(h, sel_registerName("setIgnoresMouseEvents:"), true);
                    _passthroughApplied = true;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[BalloonWindow] Passthrough: {ex.Message}"); }
        }

        // ── macOS 原生点击穿透 ──
        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, bool arg);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr sel_registerName(string name);
    }
}
