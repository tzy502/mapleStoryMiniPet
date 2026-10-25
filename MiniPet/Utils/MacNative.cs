using System;
using System.Runtime.InteropServices;

namespace MiniPet.Utils
{
    /// <summary>
    /// macOS 原生辅助（objc 消息发送，不引入 NuGet 依赖；与 BackgroundWindow 的 objc_msgSend 同模式）。
    /// 用途：隐藏程序坞（Dock）图标——NSApplicationActivationPolicyAccessory（1）：
    /// app 不进 Dock/Cmd-Tab（桌宠纯悬浮工具），但窗口照常显示、可激活输入（设置中心不受影响）。
    /// 平台限定：仅 macOS 调用（调用方已按 OperatingSystem.IsMacOS() 分流）。
    /// </summary>
    public static class MacNative
    {
        private const int NSApplicationActivationPolicyRegular = 0;
        private const int NSApplicationActivationPolicyAccessory = 1;

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr objc_getClass(string name);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, int arg);

        [DllImport("/usr/lib/libobjc.dylib", CharSet = CharSet.Ansi)]
        private static extern IntPtr sel_registerName(string name);

        /// <summary>隐藏程序坞图标（ActivationPolicy = Accessory）。失败安全 no-op。</summary>
        public static void HideDockIcon()
        {
            try
            {
                SetActivationPolicy(NSApplicationActivationPolicyAccessory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MacNative] HideDockIcon: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示程序坞图标（ActivationPolicy = Regular，Dock 出现本 app 图标 + 进 Cmd-Tab）。
        /// 运行时可反复切换（V0.1.0 发布后胶水要求 Dock 默认可见，右键菜单可切回隐藏）。失败安全 no-op。
        /// </summary>
        public static void ShowDockIcon()
        {
            try
            {
                SetActivationPolicy(NSApplicationActivationPolicyRegular);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MacNative] ShowDockIcon: {ex.Message}");
            }
        }

        /// <summary>统一入口：visible=true → Regular（Dock 可见），false → Accessory。</summary>
        public static void SetDockIconVisible(bool visible)
        {
            if (visible)
            {
                ShowDockIcon();
            }
            else
            {
                HideDockIcon();
            }
        }

        private static void SetActivationPolicy(int policy)
        {
            var nsApp = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (nsApp == IntPtr.Zero) { return; }
            objc_msgSend(nsApp, sel_registerName("setActivationPolicy:"), policy);
        }
    }
}
