using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Views.Controls;

namespace MiniPet.Views;

/// <summary>
/// PetWindow 菜单部分（partial 拆分）：右键菜单构建、动画菜单刷新、背景子菜单重建。
/// 原实现从 PetWindow.axaml.cs 平移，行为完全一致。
/// </summary>
public partial class PetWindow
{
    private MenuItem? _bgInteractiveItem;
    private MenuItem? _bgMenu;
    private MenuItem? _modeStaticItem;
    private MenuItem? _modeCameraItem;

    private ContextMenu BuildContextMenu()
    {
        var cm = new ContextMenu();
        try
        {
            // ── 切换人物 ──
            var charItem = new MenuItem { Header = "切换人物" };

            // 最近使用（最多5个）
            var settings = _configService.LoadSettings();
            if (settings.RecentItems != null && settings.RecentItems.Count > 0)
            {
                var recentItems = settings.RecentItems
                    .OrderByDescending(r => r.LastUsedAt)
                    .Take(5)
                    .ToList();
                foreach (var recent in recentItems)
                {
                    var capturedId = recent.Id;
                    var capturedName = recent.Name;
                    var capturedType = NormalizeEntityType(recent.Type);
                    var recentItem = new MenuItem { Header = $"🕐 {capturedName} [{capturedId}]" };
                    recentItem.Click += (_, _) =>
                    {
                        try { SwitchToPet(capturedId, capturedName, capturedType); }
                        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RecentSelect: {ex.Message}"); }
                    };
                    charItem.Items.Add(recentItem);
                }
                charItem.Items.Add(new Separator());
            }

            var mobSub = new MenuItem { Header = "怪物 (MOB)" };
            mobSub.Items.Add(new MenuItem { Header = "素材浏览器..." });
            (mobSub.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try
                {
                    var browser = new MaterialBrowserWindow(_wzService, _configService);
                    browser.OnMaterialSelected += (id, name) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try { SwitchToPet(id, name, "Mob"); }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BrowserSelect: {ex.Message}"); }
                        });
                    };
                    browser.Show();
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] MaterialBrowser: {ex.Message}"); }
            };
            charItem.Items.Add(mobSub);

            var npcSub = new MenuItem { Header = "NPC" };
            npcSub.Items.Add(new MenuItem { Header = "素材浏览器..." });
            (npcSub.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try
                {
                    var browser = new MaterialBrowserWindow(_wzService, _configService);
                    browser.OnMaterialSelected += (id, name) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try { SwitchToPet(id, name, "Npc"); }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BrowserSelect: {ex.Message}"); }
                        });
                    };
                    browser.Show();
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] MaterialBrowser NPC: {ex.Message}"); }
            };
            charItem.Items.Add(npcSub);

            var playerSub = new MenuItem { Header = "玩家 (PLAYER)" };
            playerSub.Items.Add(new MenuItem { Header = "纸娃娃编辑器..." });
            (playerSub.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SettingsWindow: {ex.Message}"); }
            };
            charItem.Items.Add(playerSub);
            cm.Items.Add(charItem);

            // ── 切换动画 ──
            var animItem = new MenuItem { Header = "切换动画", Name = "AnimationMenu" };
            cm.Items.Add(animItem);

            cm.Items.Add(new Separator());

            // ── 背景（合并子菜单，Opening 时动态重建：开关 + 模式单选 + 交互 + 最近×4 + 调整地图）──
            _bgMenu = new MenuItem { Header = "🎨 背景" };
            cm.Items.Add(_bgMenu);

            // ── 切换气泡 ──
            var balloonItem = new MenuItem { Header = "切换气泡" };
            balloonItem.Items.Add(new MenuItem { Header = "设置中心 > 气泡设置" });
            (balloonItem.Items[^1] as MenuItem)!.Click += (_, _) =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BalloonSettings: {ex.Message}"); }
            };
            cm.Items.Add(balloonItem);

            cm.Items.Add(new Separator());

            // ── Hermes 终端 ──
            var terminalItem = new MenuItem { Header = "Hermes 终端" };
            terminalItem.Items.Add(new MenuItem { Header = "待实现" });
            cm.Items.Add(terminalItem);

            // ── 设置中心 ──
            var settingsItem = new MenuItem { Header = "设置中心" };
            settingsItem.Click += (_, _) =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SettingsWindow: {ex.Message}"); }
            };
            cm.Items.Add(settingsItem);

            cm.Items.Add(new Separator());

            // ── 重置到桌面中央 ──
            var centerItem = new MenuItem { Header = "重置到桌面中央" };
            centerItem.Click += (_, _) =>
            {
                try
                {
                    var screens = Screens;
                    if (screens != null && screens.ScreenCount > 0)
                    {
                        var screen = screens.Primary ?? screens.All[0];
                        var bounds = screen.Bounds;
                        var w = Width;
                        var h = Height;
                        Position = new PixelPoint(
                            (int)(bounds.X + (bounds.Width - w) / 2),
                            (int)(bounds.Y + (bounds.Height - h) / 2));
                        // 地图窗口一起重置到屏幕中央（静态图/摄像机二选一，避免双模式同载）
                        if (_mapViewportWindow.IsVisible)
                        {
                            _mapViewportWindow.Position = new PixelPoint(
                                (int)(bounds.X + (bounds.Width - _mapViewportWindow.Width) / 2),
                                (int)(bounds.Y + (bounds.Height - _mapViewportWindow.Height) / 2));
                        }
                        else if (_backgroundWindow.IsVisible)
                        {
                            _backgroundWindow.Position = new PixelPoint(
                                (int)(bounds.X + (bounds.Width - _backgroundWindow.Width) / 2),
                                (int)(bounds.Y + (bounds.Height - _backgroundWindow.Height) / 2));
                            _backgroundWindow.ClampToScreen();
                        }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] CenterWindow: {ex.Message}"); }
            };
            cm.Items.Add(centerItem);

            cm.Items.Add(new Separator());

            // ── 退出 ──
            var exitItem = new MenuItem { Header = "退出" };
            exitItem.Click += (_, _) =>
            {
                try
                {
                    _isExiting = true;
                    _trayService.HideTrayIcon();
                    _settingsWindow?.Close();
                    _backgroundWindow.Close();
                    _mapViewportWindow.Close();
                    _eventBus.Publish(EventType.AppExit);
                    Close();
                    Dispatcher.UIThread.InvokeShutdown();
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PetWindow] Exit: {ex.Message}");
                    Environment.Exit(1);
                }
            };
            cm.Items.Add(exitItem);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BuildMenu: {ex.Message}"); }
        return cm;
    }

    private void RefreshAnimationMenu()
    {
        try
        {
            if (ContextMenu == null) return;

            MenuItem? animationMenu = null;
            foreach (var entry in ContextMenu.Items)
            {
                if (entry is MenuItem { Name: "AnimationMenu" } menu)
                {
                    animationMenu = menu;
                    break;
                }
            }
            if (animationMenu == null) return;

            animationMenu.Items.Clear();
            var mobId = _animService.GetCurrentMobId();
            var isSentinel = mobId == PaperdollService.SentinelMobId;
            // 哨兵优先走缓存目录枚举；否则 WZ 可用走 WZ，不可用走缓存
            var actions = isSentinel
                ? GetCachedActionList(mobId)
                : (_wzService.IsLoaded ? _wzService.GetActionList(mobId, _currentEntityType) : GetCachedActionList(mobId));

            // 注：不在打开菜单时后台预热所有动作——逐帧 PNG 解码占满 CPU 会拖垮 UI（右键菜单卡顿主因）。
            // 动作切换时按需后台解码（PreloadAction 已支持后台加载 + 完成后切换）。

            foreach (var action in actions)
            {
                var capturedAction = action;
                var item = new MenuItem { Header = capturedAction };
                item.Click += (_, _) =>
                {
                    try
                    {
                        if (isSentinel)
                        {
                            _animService.Play(capturedAction, PlayMode.Loop);
                            return;
                        }
                        // 已缓存 → 立即切换；未缓存 → 后台加载（不卡 UI），完成后切换
                        if (_spriteService.GetCachedStrip(mobId, capturedAction).Item1 != null)
                        {
                            _animService.SwitchTo(capturedAction);
                            return;
                        }
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try { _spriteService.PreloadAction(_wzService, mobId, capturedAction, _currentEntityType); }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AnimPreload {capturedAction}: {ex.Message}"); }
                            Dispatcher.UIThread.Post(() =>
                            {
                                try { _animService.SwitchTo(capturedAction); }
                                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AnimSwitch {capturedAction}: {ex.Message}"); }
                            });
                        });
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AnimSwitch {capturedAction}: {ex.Message}"); }
                };
                animationMenu.Items.Add(item);
            }

            if (actions.Count == 0)
            {
                animationMenu.Items.Add(new MenuItem { Header = "无可用动画", IsEnabled = false });
            }
            // 背景子菜单的勾选态/可用性刷新已并入 RebuildBgMenu（Opening 紧随其后重建全部 item）
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RefreshAnimationMenu: {ex.Message}"); }
    }

    /// <summary>
    /// 从磁盘缓存目录枚举 mob 的动作列表（WZ 不可用时的回退方案）
    /// </summary>

    private void RebuildBgMenu()
    {
        if (_bgMenu == null) return;
        try
        {
            _bgMenu.Items.Clear();

            // 1. 开关（背景=静态+摄像机抽象：任一可见即"开"）
            bool bgOn = _backgroundWindow.IsVisible || _mapViewportWindow.IsVisible;
            var toggle = new MenuItem { Header = bgOn ? "🚫 关闭背景" : "🌙 开启背景" };
            toggle.Click += (_, _) => ToggleBackground();
            _bgMenu.Items.Add(toggle);

            // 2. 模式单选（静态图片 / 摄像机）—— 互斥分类
            _bgMenu.Items.Add(new Separator());
            var s = _configService.LoadSettings();
            int mode = s.MapMode;
            _modeStaticItem = new MenuItem { Header = "🖼️ 静态图片", IsChecked = mode != 2 };
            _modeCameraItem = new MenuItem { Header = "📷 摄像机", IsChecked = mode == 2 };
            _modeStaticItem.Click += (_, _) => SwitchToStaticMode();
            _modeCameraItem.Click += (_, _) => SwitchToCameraMode();
            _bgMenu.Items.Add(_modeStaticItem);
            _bgMenu.Items.Add(_modeCameraItem);

            // 3. 交互模式——静态图独有（摄像机自带右键拖窗口，不需要交互模式）
            //    仅当静态图可见且当前为静态模式时可勾选
            _bgMenu.Items.Add(new Separator());
            bool interactiveUsable = _backgroundWindow.IsVisible && mode != 2;
            _bgInteractiveItem = new MenuItem { Header = "🎨 背景交互模式", IsChecked = _backgroundWindow.IsInteractive, IsEnabled = interactiveUsable };
            _bgInteractiveItem.Click += (_, _) => _backgroundWindow.SetInteractive(!_backgroundWindow.IsInteractive);
            _bgMenu.Items.Add(_bgInteractiveItem);

            // 4. 最近使用地图
            _bgMenu.Items.Add(new Separator());
            var recents = (s.MapHistory ?? new System.Collections.Generic.List<MapHistoryEntry>()).Take(4).ToList();
            if (recents.Count == 0)
            {
                _bgMenu.Items.Add(new MenuItem { Header = "（暂无最近）", IsEnabled = false });
            }
            else
            {
                foreach (var entry in recents)
                {
                    var captured = entry;
                    var name = !string.IsNullOrEmpty(entry.Name) ? entry.Name
                        : (_wzService.IsLoaded ? _wzService.GetMapName(entry.Id) : entry.Id);
                    var item = new MenuItem { Header = $"🗺️ {name} [{entry.Id}]" };
                    item.Click += (_, _) => ApplyMapSelection(new MapSelection(captured.Id, name, captured.CropX, captured.CropY, captured.CropW, captured.CropH));
                    _bgMenu.Items.Add(item);
                }
            }

            // 5. 调整地图
            _bgMenu.Items.Add(new Separator());
            var adjust = new MenuItem { Header = "🗺️ 调整地图…" };
            adjust.Click += (_, _) => OpenBackgroundTab();
            _bgMenu.Items.Add(adjust);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RebuildBgMenu: {ex.Message}"); }
    }
}
