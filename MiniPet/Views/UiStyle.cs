using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MiniPet.Views;

/// <summary>
/// 按钮悬浮（hover）统一辅助：为代码构造的 Button 挂接 PointerEntered / PointerExited / PointerCaptureLost。
/// hover 效果与输入框选中一致——背景颜色完全不变，仅边框变为 accent 蓝，移出后还原；未设光标的按钮补手型光标。
/// 不改动任何按钮的布局、字体、背景色与点击逻辑。
/// </summary>
public static class UiStyle
{
    /// <summary>hover 边框色 = accent 蓝（与项目输入框选中色一致：SettingsWindow AccentColor 94,156,255）。</summary>
    private static readonly Color HoverBorderColor = Color.FromArgb(255, 94, 156, 255);

    /// <summary>已挂接按钮表（弱引用，防泄漏），用于幂等去重：同一按钮重复调用只挂接一次。</summary>
    private static readonly ConditionalWeakTable<Button, object> AttachedButtons = new();

    /// <summary>
    /// 为按钮挂接统一悬浮效果：hover 时边框变 accent 蓝（背景不动），移出还原；幂等。
    /// </summary>
    public static void ApplyHover(Button btn)
    {
        if (btn == null) return;
        // 幂等：已挂接过的按钮直接返回
        if (AttachedButtons.TryGetValue(btn, out _)) return;
        AttachedButtons.Add(btn, new object());

        // 未设光标的按钮补手型光标（悬浮反馈的第一直觉：指针变手）
        if (btn.Cursor == null)
        {
            btn.Cursor = new Cursor(StandardCursorType.Hand);
        }

        // 挂接时捕获基准边框引用（不依赖按钮运行时当前值——按钮样式可能在运行中被代码修改）
        IBrush? baseBorder = btn.BorderBrush;
        // 本次悬浮应用过的 hover 边框引用（用于还原时判断「当前值是否是我们挂上的」）
        IBrush? hoverBorder = null;
        var hoverBorderBrush = new SolidColorBrush(HoverBorderColor);

        btn.PointerEntered += (_, _) =>
        {
            // 运行时样式可能被改：当前值既不是基准也不是我们上次的悬浮值 → 采纳为新基准
            if (!ReferenceEquals(btn.BorderBrush, baseBorder) && !ReferenceEquals(btn.BorderBrush, hoverBorder))
            {
                baseBorder = btn.BorderBrush;
            }

            // 背景保持原样（用户要求 hover 不变色）；仅边框变 accent 蓝（与输入框选中一致）
            hoverBorder = hoverBorderBrush;
            btn.BorderBrush = hoverBorder;
        };

        btn.PointerExited += (_, _) => Restore(btn, ref baseBorder, ref hoverBorder);
        // 指针捕获丢失（如拖拽中途被其他元素抢捕获）同样还原悬浮效果
        btn.PointerCaptureLost += (_, _) => Restore(btn, ref baseBorder, ref hoverBorder);
    }

    /// <summary>移出/丢失指针时还原：仅当当前边框是我们挂上的 hover 边框才还原基准值，被代码改过的值不动。</summary>
    private static void Restore(Button btn, ref IBrush? baseBorder, ref IBrush? hoverBorder)
    {
        if (ReferenceEquals(btn.BorderBrush, hoverBorder))
        {
            btn.BorderBrush = baseBorder;
        }
    }
}
