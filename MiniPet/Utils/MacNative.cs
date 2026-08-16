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
                var nsApp = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (nsApp == IntPtr.Zero) { return; }
                objc_msgSend(nsApp, sel_registerName("setActivationPolicy:"), NSApplicationActivationPolicyAccessory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MacNative] HideDockIcon: {ex.Message}");
            }
        }
    }
}
