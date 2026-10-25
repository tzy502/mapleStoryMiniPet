using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;
#if !NO_AI
using MiniPet.Services.Adapters;
#endif
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
    private MenuItem? _paperdollItem;

    // 音乐模块 V0.2.1：右键菜单「🎵 迷你控制条」开关项（IsChecked 反映 MusicConfig.MiniBarEnabled，见 RefreshMiniBarMenuItem）
    private MenuItem? _miniBarItem;
    // V0.1.3 多桌宠菜单项
    private MenuItem? _addPetMenuItem;
    private MenuItem? _primaryPetMenuItem;
    private MenuItem? _removePetMenuItem;


    private ContextMenu BuildContextMenu()
    {
        var cm = new ContextMenu();
        try
        {
            // ── 🧍 外观（折叠：切换人物 / 切换动画 / 😊 表情 / 左右转向 / 切换气泡）──
            // 胶水 2026-09-24：右键菜单顶层太长，按内容折叠
            var appearanceMenu = new MenuItem { Header = "🧍 外观" };
            cm.Items.Add(appearanceMenu);

            // ── 切换人物 ──
            var charItem = new MenuItem { Header = "切换人物" };

            // 纸娃娃入口（独立分组置顶）：恢复当前预设搭配；已处于纸娃娃模式时由 RefreshPaperdollMenuItem 改为「✓ 纸娃娃（当前）」并禁用
            _paperdollItem = new MenuItem { Header = "🧍 纸娃娃（当前预设）" };
            _paperdollItem.Click += (_, _) =>
            {
                try { SwitchToPaperdoll(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollMenuSelect: {ex.Message}"); }
            };
            charItem.Items.Add(_paperdollItem);
            charItem.Items.Add(new Separator());

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
                    var browser = new MaterialBrowserWindow(_wzService, _configService, _paperdoll);
                    browser.OnMaterialSelected += (id, name, _) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try { SwitchToPet(id, name, "Mob"); }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BrowserSelect: {ex.Message}"); }
                        });
                    };
                    browser.OnPaperdollDraftApplied += appearance =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try { ApplyPaperdoll(appearance); }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollDraftApply: {ex.Message}"); }
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
                    var browser = new MaterialBrowserWindow(_wzService, _configService, _paperdoll);
                    browser.OnMaterialSelected += (id, name, _) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try { SwitchToPet(id, name, "Npc"); }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BrowserSelect: {ex.Message}"); }
                        });
                    };
                    browser.OnPaperdollDraftApplied += appearance =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try { ApplyPaperdoll(appearance); }
                            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PaperdollDraftApply: {ex.Message}"); }
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
            appearanceMenu.Items.Add(charItem);

            // ── 切换动画 ──
            var animItem = new MenuItem { Header = "切换动画", Name = "AnimationMenu" };
            appearanceMenu.Items.Add(animItem);

            // ── 😊 表情（仅纸娃娃；Opening 动态重建，见 RefreshExpressionMenu）──
            _exprMenu = new MenuItem { Header = "😊 表情", Name = "ExpressionMenu" };
            appearanceMenu.Items.Add(_exprMenu);

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
            // ── 💬 随机气泡发言开关（胶水 2026-09-16：放在「切换气泡」下）──
            var balloonToggle = new MenuItem
            {
                Header = "💬 随机气泡发言",
                IsChecked = _configService.LoadSettings().RandomBalloonEnabled,
                ToggleType = Avalonia.Controls.MenuItemToggleType.CheckBox,
            };
            balloonToggle.Click += (_, _) =>
            {
                try
                {
                    bool newVal = !_configService.LoadSettings().RandomBalloonEnabled;
                    _configService.UpdateSettings(st => st.RandomBalloonEnabled = newVal);
                    balloonToggle.IsChecked = newVal;
                    ApplyRandomBalloonEnabled(newVal);
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BalloonToggle: {ex.Message}"); }
            };
            appearanceMenu.Items.Add(balloonToggle);
            appearanceMenu.Items.Add(balloonItem);

            // ── 🎵 迷你控制条（音乐模块 V0.2.1）：开关勾选项，控制 B2 的 MusicMiniBarWindow 显隐；
            //    勾选态 = 配置 MiniBarEnabled（Opening 时经 RefreshMiniBarMenuItem 再同步一次），
            //    点击经 ConfigService.Update 事务翻转落库 + EventBus 发布 minibar-changed 通知刷新 ──
            _miniBarItem = new MenuItem
            {
                Header = "🎵 迷你控制条",
                // 兼容旧配置 music 字段缺省/显式 null：默认不勾选
                IsChecked = _configService.LoadSettings().Music?.MiniBarEnabled ?? false,
            };
            _miniBarItem.Click += (_, _) =>
            {
                try
                {
                    // 第一步：目标值 = 当前配置取反（先算好再落库，Update 事务体闭包持有）
                    var target = !(_configService.LoadSettings().Music?.MiniBarEnabled ?? false);
                    // 第二步：Update 事务持久化（UI 线程 _lock 内 读→改→写 整事务；music 子对象兜底非空）
                    _configService.Update(s => { s.Music ??= new MusicConfig(); s.Music.MiniBarEnabled = target; });
                    // 第三步：同步本项勾选态 + EventBus 发布开关变化（payload=bool 最新值）通知迷你条窗口
                    _miniBarItem.IsChecked = target;
                    _eventBus.Publish(MiniPetEvents.MiniBarChanged, target);
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] MiniBarToggle: {ex.Message}"); }
            };
            // 折叠成「🎵 音乐」子菜单（胶水 2026-09-24：合并成音乐；后续音乐项都放这里）
            var musicMenu = new MenuItem { Header = "🎵 音乐" };
            musicMenu.Items.Add(_miniBarItem);
            cm.Items.Add(musicMenu);

            cm.Items.Add(new Separator());

#if !NO_AI
            // ── 🤖 AI（折叠：Hermes 终端 / 对话 / DSH 对话 / DSH 状态，胶水 2026-09-24 要求收起）──
            // NO_AI 纯净版不出现该菜单（AI 窗口/适配器均已从编译剔除）
            var aiMenu = new MenuItem { Header = "🤖 AI" };

            // Hermes 终端（快速唤起 hermes CLI + 显示 Agent 反映态）
            var terminalItem = new MenuItem { Header = "Hermes 终端" };
            var launchItem = new MenuItem { Header = "🚀 启动 hermes" };
            launchItem.Click += (_, _) =>
            {
                try { LaunchHermesCli(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] LaunchHermes: {ex.Message}"); }
            };
            terminalItem.Items.Add(launchItem);
            var statusItem = new MenuItem { Header = "📊 显示状态" };
            statusItem.Click += (_, _) =>
            {
                try { ShowAgentStatusPopup(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AgentStatus: {ex.Message}"); }
            };
            terminalItem.Items.Add(statusItem);
            aiMenu.Items.Add(terminalItem);

            // ── Hermes 对话（迷你聊天对话框，PLAN §3；单例，已开则 Activate/聚焦） ──
            var chatItem = new MenuItem { Header = "💬 对话" };
            chatItem.Click += (_, _) =>
            {
                try { HermesChatWindow.OpenOrActivate(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] HermesChat: {ex.Message}"); }
            };
            aiMenu.Items.Add(chatItem);

            // ── DSH 对话（DeepSeek Harness 桥接，feat/dsh-bridge；单例，已开则 Activate/聚焦）──
            // 对话链路独立于「观察开关」：未装配（DshEnabled 关）时点击自动装配 DshAdapter
            // （连 ws://127.0.0.1:3080/api/events.mux 收帧，session.create 固定 minipet_dsh 复用），
            // 无需先去设置开启观察即可对话；DSH 未运行则窗口状态行显示 🔴 未连接。
            var dshChatItem = new MenuItem { Header = "🤖 DSH 对话" };
            dshChatItem.Click += (_, _) =>
            {
                try
                {
                    EnsureDshAdapterForChat();
                    DshChatWindow.OpenOrActivate();
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] DshChat: {ex.Message}"); }
            };
            aiMenu.Items.Add(dshChatItem);

            // ── DSH 状态（监控是默认行为：默认开启时桌宠反映 DSH 干活状态；弹窗只读显示连接 + 反映态，不装配 adapter）──
            var dshStatusItem = new MenuItem { Header = "📊 DSH 状态" };
            dshStatusItem.Click += (_, _) =>
            {
                try { ShowDshStatusPopup(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] DshStatus: {ex.Message}"); }
            };
            aiMenu.Items.Add(dshStatusItem);

            cm.Items.Add(aiMenu);   // 折叠成一个「🤖 AI」子菜单（顶层不再铺开）
#endif // !NO_AI

            // ── V0.1.3 多桌宠：添加 / 移除 / 设为主宠 ──
            var petMenuItem = new MenuItem { Header = "🐾 桌宠" };
            _addPetMenuItem = new MenuItem { Header = "➕ 添加桌宠（复制这只）" };
            _addPetMenuItem.Click += (_, _) =>
            {
                try { _eventBus.Publish(EventType.AddPet, new PetCommandEvent(_petId)); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AddPet: {ex.Message}"); }
            };
            petMenuItem.Items.Add(_addPetMenuItem);

            _primaryPetMenuItem = new MenuItem { Header = "⭐ 设为主宠" };
            _primaryPetMenuItem.Click += (_, _) =>
            {
                try { _eventBus.Publish(EventType.SetPrimaryPet, new PetCommandEvent(_petId)); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SetPrimaryPet: {ex.Message}"); }
            };
            petMenuItem.Items.Add(_primaryPetMenuItem);

            _removePetMenuItem = new MenuItem { Header = "🗑 移除这只桌宠" };
            _removePetMenuItem.Click += (_, _) =>
            {
                try { _eventBus.Publish(EventType.RemovePet, new PetCommandEvent(_petId)); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RemovePet: {ex.Message}"); }
            };
            petMenuItem.Items.Add(_removePetMenuItem);

            cm.Items.Add(petMenuItem);
            cm.Items.Add(new Separator());

            // ── 设置中心 ──
            var settingsItem = new MenuItem { Header = "设置中心" };
            settingsItem.Click += (_, _) =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SettingsWindow: {ex.Message}"); }
            };
            cm.Items.Add(settingsItem);

            // ── 重置到桌面中央（收进「🐾 桌宠」）──
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
            petMenuItem.Items.Add(centerItem);   // 收进「🐾 桌宠」

            // ── 📌 置顶（胶水 2026-09-24）──
            var topmostItem = new MenuItem
            {
                Header = (_configService.GetTopmost() ? "✓ " : "   ") + "📌 置顶",
            };
            topmostItem.Click += (_, _) =>
            {
                try
                {
                    bool v = !_configService.GetTopmost();
                    _configService.SetTopmost(v);
                    // 胶水 2026-09-24：置顶要对**所有**桌宠生效（主子都要），不只当前这只
                    try
                    {
                        foreach (var pet in _petManager.Pets) { pet.Topmost = v; }
                    }
                    catch (Exception ex2)
                    {
                        Console.Error.WriteLine($"[PetWindow] Topmost(all pets): {ex2.Message}");
                        Topmost = v;
                    }
                    topmostItem.Header = (v ? "✓ " : "   ") + "📌 置顶";
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] TopmostToggle: {ex.Message}"); }
            };
            petMenuItem.Items.Add(topmostItem);

            // ── 🚢 Dock 图标（仅 macOS；V0.1.0 对外发布默认开，可随时切回旧版隐藏行为）──
            if (OperatingSystem.IsMacOS())
            {
                var dockItem = new MenuItem
                {
                    Header = (_configService.GetShowDockIcon() ? "✓ " : "   ") + "🚢 Dock 图标",
                };
                dockItem.Click += (_, _) =>
                {
                    try
                    {
                        bool v = !_configService.GetShowDockIcon();
                        _configService.SetShowDockIcon(v);
                        MiniPet.Utils.MacNative.SetDockIconVisible(v);
                        dockItem.Header = (v ? "✓ " : "   ") + "🚢 Dock 图标";
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] DockIconToggle: {ex.Message}"); }
                };
                petMenuItem.Items.Add(dockItem);
            }

            // ── 退出 ──
            var exitItem = new MenuItem { Header = "退出" };
            exitItem.Click += (_, _) =>
            {
                try
                {
                    _isExiting = true;
                    _trayService.HideTrayIcon();
                    // V0.1.3：设置中心全局唯一（由 PetManager 持有），随进程一起退出，不在这里关
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
            // ── 左右转向（胶水 2026-09-16：所有桌宠每个都可以独立转向）──
            var flipItem = new MenuItem { Header = "↔ 转向" };
            flipItem.Click += (_, _) =>
            {
                try { FlipFacing(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] FlipFacing: {ex.Message}"); }
            };
            appearanceMenu.Items.Add(flipItem);


            // ── 播放音乐时加载地图（原「行走显示地图背景」；胶水 2026-09-24：移到🎵音乐下并改文案）──
            var mapBgItem = new MenuItem
            {
                Header = "🗺️ 播放音乐时加载地图",
                IsChecked = _configService.LoadSettings().WalkShowMapBackground,
                ToggleType = Avalonia.Controls.MenuItemToggleType.CheckBox,
            };
            mapBgItem.Click += (_, _) =>
            {
                try
                {
                    bool newVal = !_configService.LoadSettings().WalkShowMapBackground;
                    _configService.UpdateSettings(st => st.WalkShowMapBackground = newVal);
                    mapBgItem.IsChecked = newVal;
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] MapBgToggle: {ex.Message}"); }
            };
            musicMenu.Items.Add(mapBgItem);   // 收进「🎵 音乐」

            cm.Items.Add(new Separator());
            cm.Items.Add(exitItem);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] BuildMenu: {ex.Message}"); }
        return cm;
    }

    /// <summary>
    /// 刷新「🎵 迷你控制条」勾选态（ContextMenu.Opening 时调用，与 MiniBarEnabled 配置保持同步：
    /// 覆盖设置页等其他写入点的变更；初始勾选已在构建时读配置）。
    /// </summary>
    private void RefreshMiniBarMenuItem()
    {
        if (_miniBarItem == null) { return; }
        try
        {
            _miniBarItem.IsChecked = _configService.LoadSettings().Music?.MiniBarEnabled ?? false;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RefreshMiniBarMenuItem: {ex.Message}"); }
    }

    /// <summary>
    /// 刷新「切换人物」纸娃娃菜单项状态（ContextMenu.Opening 时调用）：
    /// 已处于纸娃娃模式 → 显示「✓ 纸娃娃（当前）」并禁用；未保存预设 → 显示「无预设」并禁用。
    /// </summary>
    private void RefreshPaperdollMenuItem()
    {
        if (_paperdollItem == null) { return; }
        try
        {
            var settings = _configService.LoadSettings();
            // V0.1.3：看本宠物自己的搭配是否存在
            var hasPreset = ((settings.Pets?.FirstOrDefault(p => p.Id == _petId)?.Paperdoll) ?? _cfg.Paperdoll) != null;
            if (IsSentinelActive())
            {
                _paperdollItem.Header = "✓ 纸娃娃（当前）";
                _paperdollItem.IsEnabled = false;
            }
            else
            {
                _paperdollItem.Header = hasPreset ? "🧍 纸娃娃（当前预设）" : "🧍 纸娃娃（无预设）";
                _paperdollItem.IsEnabled = hasPreset;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RefreshPaperdollMenuItem: {ex.Message}"); }
    }

    /// <summary>
    /// 刷新「桌宠」菜单（ContextMenu.Opening 时调用，V0.1.3）：
    /// 当前主宠标记为 ✓ 并禁用重复设置；仅剩一只时禁用移除。
    /// </summary>
    private void RefreshPetMenu()
    {
        try
        {
            var count = _petManager?.Count ?? 1;
            if (_primaryPetMenuItem != null)
            {
                _primaryPetMenuItem.Header = _isPrimaryRole ? "✓ 主宠（当前）" : "⭐ 设为主宠";
                _primaryPetMenuItem.IsEnabled = !_isPrimaryRole;
            }
            if (_removePetMenuItem != null)
            {
                _removePetMenuItem.Header = count > 1 ? "🗑 移除这只桌宠" : "🗑 移除这只桌宠（仅剩一只，不可移除）";
                _removePetMenuItem.IsEnabled = count > 1;
            }
            if (_addPetMenuItem != null)
            {
                _addPetMenuItem.Header = $"➕ 添加桌宠（当前 {count} 只）";
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RefreshPetMenu: {ex.Message}"); }
    }

    /// <summary>
    /// 切换到纸娃娃模式：恢复当前预设搭配（复用 LoadDefaultPet 启动恢复路径的 SetAppearance + 哨兵 mobId），
    /// 持久化哨兵以便重启恢复，循环播放 stand1（同 ApplyPaperdoll 完成路径），后台预热常用动作。
    /// </summary>
    private void SwitchToPaperdoll()
    {
        try
        {
            var settings = _configService.LoadSettings();
            // V0.1.3：本宠物自己的纸娃娃搭配（旧版读全局 CurrentPaperdoll = 所有宠物共用一套外观）
            var pa = (settings.Pets?.FirstOrDefault(p => p.Id == _petId)?.Paperdoll) ?? _cfg.Paperdoll;
            if (pa == null)
            {
                ShowInfoPopup("切换人物", "未保存纸娃娃预设。\n请先在设置中心 > 玩家 (PLAYER) > 纸娃娃编辑器 保存搭配。");
                return;
            }
            if (IsSentinelActive()) { return; } // 已在纸娃娃模式（菜单项已禁用，双保险）
            _currentEntityType = "player";
            _paperdoll.SetAppearance(pa); // 同启动恢复：切换外观清依赖外观缓存；引用赋值原子，帧循环安全
            // 持久化哨兵（V0.1.3：只写本宠物这一只的实例配置）
            PersistEntity(PaperdollService.SentinelMobId, "player");
            _animService.SetCurrentMobId(PaperdollService.SentinelMobId);
            _animService.Play("stand1", PlayMode.Loop);
            _spriteCanvas.ForceSizeRefresh(); // 切换素材强制容器重设（防截断，同 SwitchToPet）
            _lastFrameW = 0; _lastFrameH = 0;
            _trayService.SetTooltip("MiniPet - 纸娃娃");
            // 后台预热常用动作缓存（避免恢复后首帧合成卡顿抖动，同 LoadDefaultPet）
            System.Threading.Tasks.Task.Run(() => { try { _paperdoll.Warmup(); } catch { } });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SwitchToPaperdoll: {ex.Message}"); }
    }

#if !NO_AI
    /// <summary>
    /// 快速唤起 hermes CLI（UseShellExecute=true，让系统按 PATH 解析并独立窗口运行）。
    /// 失败（未安装/不在 PATH）→ 提示用户。
    /// </summary>
    private void LaunchHermesCli()
    {
        try
        {
            var psi = new ProcessStartInfo("hermes")
            {
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetWindow] LaunchHermesCli: {ex.Message}");
            ShowInfoPopup("Hermes 终端", "未找到 hermes 命令。\n请先安装 Hermes CLI 并确认其在系统 PATH 中。");
        }
    }

    /// <summary>
    /// Agent 状态弹窗：当前 Aggregator 反映态 + 各 adapter 开关。
    /// 注：Aggregator 尚未注入 PetWindow（PLAN 阶段2 接线），此处读 AgentIntegration 配置展示当前接入态，
    /// 反映态字段留接口，后续接 EffectLayerService / AgentStateAggregator.CurrentReflect。
    /// </summary>
    private void ShowAgentStatusPopup()
    {
        var settings = _configService.LoadSettings();
        var ai = settings.AgentIntegration ?? new AgentIntegrationConfig();

        var lines = new List<string>
        {
            "🤖 Agent 接入状态",
            string.Empty,
            $"  Hermes   : {(ai.HermesEnabled ? "开" : "关")}",
            $"  Claude Code : {(ai.CCEnabled ? "开" : "关")}",
            $"  Codex    : {(ai.CodexEnabled ? "开" : "关")}",
            $"  特效层   : {(ai.EffectEnabled ? "开" : "关")}",
            string.Empty,
            $"  轮询周期 : {ai.HermesPollIntervalSeconds}s",
            $"  静默窗   : {ai.CCSilenceSeconds}s",
            $"  Staleness: {ai.StalenessSeconds}s",
            string.Empty,
            "  反映态   : Idle（待 Aggregator 接线）",
        };
        ShowInfoPopup("Agent 状态", string.Join("\n", lines));
    }

    /// <summary>
    /// DSH 状态弹窗：监控开关 + 连接状态 + 当前 DSH 反映态（只读，不装配 adapter）。
    /// 监控为默认行为（DshEnabled 默认开）；连接状态来自 DshAdapter 最近一次 ConnStatus。
    /// </summary>
    private void ShowDshStatusPopup()
    {
        var settings = _configService.LoadSettings();
        var ai = settings.AgentIntegration ?? new AgentIntegrationConfig();
        var dsh = DshAdapter.Current;

        var lines = new List<string>
        {
            "🤖 DSH 监控状态",
            string.Empty,
            $"  监控开关 : {(ai.DshEnabled ? "开" : "关")}",
            $"  连接状态 : {(dsh != null && dsh.IsConnected ? "🟢 已连接" : "🔴 未连接（静默退避重连中）")}",
            string.Empty,
            $"  当前反映态 : {(dsh == null ? "—（未装配）" : DshStateName(dsh.LastPublishedState))}",
            string.Empty,
            "  DSH 干活时桌宠自动反映（Run→move 动作等，走 EffectLayerService 链路）；",
            "  仅本机 loopback，隐私只消费事件视图非内容字段。",
        };
        ShowInfoPopup("DSH 状态", string.Join("\n", lines));
    }

    /// <summary>DSH 反映态中文名（与设置页 7 态映射区一致）。</summary>
    private static string DshStateName(AgentState s)
    {
        switch (s)
        {
            case AgentState.Run: return "Run 干活";
            case AgentState.Review: return "Review 思考";
            case AgentState.Wave: return "Wave 完成";
            case AgentState.Jump: return "Jump 庆祝";
            case AgentState.Failed: return "Failed 失败";
            case AgentState.Waiting: return "Waiting 等待";
            default: return "Idle 空闲";
        }
    }

    /// <summary>
    /// 保证 DSH 对话可用：DshAdapter 未装配（设置页 DshEnabled 关）时自动装配一个实例并 Start。
    /// 对话链路独立于观察开关——点击「🤖 DSH 对话」无需先去设置开启观察；
    /// 自动装配的实例同样走 EventBus 发布状态（桌宠反映 DSH 干活过程，Aggregator 未装配时无害）。
    /// 幂等：DshAdapter.Current 已存在（App 启动装配或本方法已装配）则直接返回。
    /// </summary>
    private void EnsureDshAdapterForChat()
    {
        if (DshAdapter.Current != null) { return; }
        try
        {
            var ai = _configService.LoadSettings().AgentIntegration ?? new AgentIntegrationConfig();
            var dsh = new DshAdapter(_eventBus, ai);
            dsh.Start();
            var host = string.IsNullOrWhiteSpace(ai.DshHost) ? "127.0.0.1" : ai.DshHost.Trim();
            var port = ai.DshPort == 0 ? 3080 : ai.DshPort;
            Console.WriteLine($"[PetWindow] DSH 对话自动装配: ws://{host}:{port}/api/events.mux");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetWindow] DSH 对话自动装配失败: {ex.Message}");
            ShowInfoPopup("DSH 对话", "DSH 接入初始化失败。\n请确认本机 DSH（DeepSeek Harness）已运行（127.0.0.1:3080），\n或在 设置中心 → Agent 接入 → DSH 连接 检查配置后重试。");
        }
    }
#endif // !NO_AI

    /// <summary>简单的信息弹窗（自包含，无第三方依赖）：标题 + 多行文本 + 关闭按钮。</summary>
    private void ShowInfoPopup(string title, string body)
    {
        try
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 260,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 38)),
                // 自适应：允许缩放（Agent 状态 10+ 行文本可能超 260 高被裁）
                CanResize = true,
            };
            var stack = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
            };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 244)),
            });
            stack.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 200, 210)),
            });
            var closeBtn = new Button
            {
                Content = "关闭",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Padding = new Thickness(20, 6),
            };
            UiStyle.ApplyHover(closeBtn);
            closeBtn.Click += (_, _) => win.Close();
            stack.Children.Add(closeBtn);
            // 自适应：内容包 ScrollViewer，窗口矮时滚动查看不裁切
            win.Content = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            win.Show();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ShowInfoPopup: {ex.Message}"); }
    }

    /// <summary>
    /// 表情子菜单（V0.2.x M1，见 docs/ai/PLAN-表情功能.md §4.3）：
    /// 仅纸娃娃哨兵模式显示；按 KnownExpressions 列中文名 + key；点击只换脸图层，不打断 body 动作。
    /// 尾部：恢复默认 + 自动眨眼/随机表情开关（M2）。
    /// </summary>
    private void RefreshExpressionMenu()
    {
        try
        {
            if (_exprMenu == null) { return; }
            _exprMenu.Items.Clear();
            // 表情菜单**始终显示**（胶水 2026-09-24：怪物/NPC 模式下整项消失，看着像"内容没了"）——
            // 非纸娃娃模式给出说明项，而不是隐藏整项。
            var isSentinel = _animService.GetCurrentMobId() == PaperdollService.SentinelMobId;
            _exprMenu.IsVisible = true;
            if (!isSentinel)
            {
                var tip = new MenuItem { Header = "（仅纸娃娃支持表情）", IsEnabled = false };
                _exprMenu.Items.Add(tip);
                var back = new MenuItem { Header = "切到纸娃娃" };
                back.Click += (_, _) =>
                {
                    try { SwitchToPaperdoll(); }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] 切纸娃娃: {ex.Message}"); }
                };
                _exprMenu.Items.Add(back);
                return;
            }

            // 开关以**本地配置**为准：每次打开菜单同步一次（只读赋值，不触发落盘）
            try
            {
                _paperdoll.AutoBlinkEnabled = _configService.GetAutoBlink();
                _paperdoll.RandomExpressionEnabled = _configService.GetRandomExpression();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] 读取表情开关: {ex.Message}"); }

            foreach (var (key, cn, _) in PaperdollService.KnownExpressions)
            {
                if (string.Equals(key, PaperdollService.ExpressionDefault, StringComparison.Ordinal))
                {
                    continue;
                }
                var item = new MenuItem { Header = $"{cn} ({key})" };
                string captured = key;
                item.Click += (_, _) =>
                {
                    try
                    {
                        // 眨眼是 3 帧快动画（≈180ms），给 3s 会「循环眨眼好几秒」（验收 §7-2）；其余表情 3s
                        int ms = string.Equals(captured, PaperdollService.ExpressionBlink, StringComparison.Ordinal) ? 200 : 3000;
                        _paperdoll.PlayExpression(captured, ms);
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PlayExpression({captured}): {ex.Message}"); }
                };
                _exprMenu.Items.Add(item);
            }

            _exprMenu.Items.Add(new Separator());
            var reset = new MenuItem { Header = "恢复默认" };
            reset.Click += (_, _) =>
            {
                try { _paperdoll.CancelExpression(); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] CancelExpression: {ex.Message}"); }
            };
            _exprMenu.Items.Add(reset);

            _exprMenu.Items.Add(new Separator());
            // 勾选用文本前缀（Avalonia 12.1 无 MenuItemToggleType；文本标记最稳且视觉清晰）
            var blink = new MenuItem
            {
                Header = (_paperdoll.AutoBlinkEnabled ? "✓ " : "   ") + "自动眨眼",
            };
            blink.Click += (_, _) =>
            {
                try { ApplyExpressionSettings(!_paperdoll.AutoBlinkEnabled, _paperdoll.RandomExpressionEnabled); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] AutoBlink: {ex.Message}"); }
            };
            _exprMenu.Items.Add(blink);

            var rnd = new MenuItem
            {
                Header = (_paperdoll.RandomExpressionEnabled ? "✓ " : "   ") + "随机表情",
            };
            rnd.Click += (_, _) =>
            {
                try { ApplyExpressionSettings(_paperdoll.AutoBlinkEnabled, !_paperdoll.RandomExpressionEnabled); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RandomExpression: {ex.Message}"); }
            };
            _exprMenu.Items.Add(rnd);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RefreshExpressionMenu: {ex.Message}"); }
    }

    private void RefreshAnimationMenu()
    {
        try
        {
            if (ContextMenu == null) { return; }

            // ⚠️ 必须**递归**查找：菜单折叠后「切换动画」位于「🧍 外观」子菜单内，只遍历顶层会找不到
            // → animationMenu 为 null → 直接 return → 动画菜单永远空、人物只能停在 stand
            //（胶水 2026-09-24 报「动画没有了 / 动画加载也没成功 / 人物默认 stand」）。
            MenuItem? animationMenu = null;
            void FindMenu(IEnumerable<object?> items)
            {
                foreach (var entry in items)
                {
                    if (animationMenu != null) { return; }
                    if (entry is not MenuItem mi) { continue; }
                    if (mi.Name == "AnimationMenu") { animationMenu = mi; return; }
                    FindMenu(mi.Items);
                }
            }
            FindMenu(ContextMenu.Items);
            if (animationMenu == null) { return; }

            animationMenu.Items.Clear();
            var mobId = _animService.GetCurrentMobId();
            var isSentinel = mobId == PaperdollService.SentinelMobId;
            // 纸娃娃：MapleSalon2 动作集（有效帧，walk/fly 优先 + 简体中文名）；否则 WZ 可用走 WZ，不可用走缓存
            var actions = isSentinel
                ? OrderPaperdollActions(_paperdoll.GetAvailableActions())
                : (_wzService.IsLoaded ? _wzService.GetActionList(mobId, _currentEntityType) : GetCachedActionList(mobId));

            // 注：不在打开菜单时后台预热所有动作——逐帧 PNG 解码占满 CPU 会拖垮 UI（右键菜单卡顿主因）。
            // 动作切换时按需后台解码（PreloadAction 已支持后台加载 + 完成后切换）。

            foreach (var action in actions)
            {
                var capturedAction = action;
                var header = isSentinel && PaperdollService.ActionNames.TryGetValue(capturedAction, out var cn) ? cn : capturedAction;
                var item = new MenuItem { Header = header };
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
            if (isSentinel)
            {
                // 面向（左/右）：纸娃娃翻转渲染
                animationMenu.Items.Add(new Separator());
                var flipItem = new MenuItem { Header = _paperdoll.Flip ? "面向：左" : "面向：右" };
                flipItem.Click += (_, _) => { try { _paperdoll.Flip = !_paperdoll.Flip; Console.WriteLine($"[PetWindow] 面向: {(_paperdoll.Flip ? "左" : "右")}"); } catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] Flip: {ex.Message}"); } };
                animationMenu.Items.Add(flipItem);
            }
            // 背景子菜单的勾选态/可用性刷新已并入 RebuildBgMenu（Opening 紧随其后重建全部 item）
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RefreshAnimationMenu: {ex.Message}"); }
    }

    /// <summary>纸娃娃动作排序：walk/fly 优先（用户要求默认先 walk1/walk2/fly），其余按常用顺序。</summary>
    private static List<string> OrderPaperdollActions(List<string> actions)
    {
        var priority = new[] { "walk1", "walk2", "fly", "stand1", "stand2", "alert", "heal", "jump", "sit", "ladder", "rope", "prone", "proneStab" };
        var result = new List<string>();
        foreach (var p in priority)
            if (actions.Contains(p)) { result.Add(p); }
        foreach (var a in actions)
            if (!result.Contains(a)) { result.Add(a); }
        return result;
    }

    /// <summary>
    /// 从磁盘缓存目录枚举 mob 的动作列表（WZ 不可用时的回退方案）
    /// </summary>

    private void RebuildBgMenu()
    {
        if (_bgMenu == null) { return; }
        try
        {
            _bgMenu.Items.Clear();

            // V0.1.3 多桌宠：地图背景只挂主宠（背景窗口是「全局单份」语义——同一张地图 + 全局层开关，
            // 多只同时挂会互相遮挡，且 MapService 整图缓存单槽会互相顶掉）。
            if (!_isPrimaryRole)
            {
                _bgMenu.Items.Add(new MenuItem { Header = "（仅主宠可挂地图背景）", IsEnabled = false });
                return;
            }

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
                    // 存量名 == id / map_ 哨兵 → 重新查缓存（修「历史菜单中文缺失」，如 10000/1000000）
                    var name = _wzService.IsLoaded
                        ? _wzService.ResolveDisplayName(entry.Id, entry.Name)
                        : (!string.IsNullOrEmpty(entry.Name) ? entry.Name : entry.Id);
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
