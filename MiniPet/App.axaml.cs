using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Adapters;
using MiniPet.Views;
using Avalonia.Themes.Fluent;

namespace MiniPet;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    // Agent 接入链（PLAN-agent-integration §1/§3）。关闭时统一 Stop，避免轮询/FSW 泄露。
    private AgentStateAggregator? _agentAggregator;
    private EffectLayerService? _effectLayer;
    private readonly List<IAgentStateAdapter> _agentAdapters = new();

    public App()
    {
        // App.axaml 已删（纯代码模式），FluentTheme 在这里注册，替代 <Application.Styles>
        Styles.Add(new FluentTheme());

        // Fluent 主题的 Button 模板自带 hover 高亮（模板内 ContentPresenter:pointerover 会覆盖
        // Button 的本地 Background/BorderBrush，优先级高于本地值）——在此覆写为恒跟随 Button 的
        // Background/BorderBrush：按钮 hover 效果完全由 UiStyle 控制（背景不变 + 边框变 accent 蓝，
        // 与输入框选中一致），消除主题自带的 hover 变色。
        Styles.Add(new Style(x => x.OfType<Button>().Class(":pointerover")
            .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, new TemplateBinding(Button.BackgroundProperty)),
                new Setter(ContentPresenter.BorderBrushProperty, new TemplateBinding(Button.BorderBrushProperty)),
            }
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 2026-08-17 用户要求：macOS 桌宠不进程序坞（Dock）——ActivationPolicy=Accessory，
        // 窗口照常显示/可输入（设置中心不受影响）；Windows 任务栏隐藏见 PetWindow.ShowInTaskbar。
        if (OperatingSystem.IsMacOS())
        {
            try { Utils.MacNative.HideDockIcon(); }
            catch (Exception ex) { Console.Error.WriteLine($"[App] HideDockIcon: {ex.Message}"); }
        }
        var services = new ServiceCollection();
        services.AddSingleton<ConfigService>();
        services.AddSingleton<CacheManager>();
        services.AddSingleton<WzService>();
        services.AddSingleton<SpriteService>();
        services.AddSingleton<EventBus>();
        // TrayService 仅在 Windows 平台有效，非 Windows 平台内部降级为 no-op
        services.AddSingleton<TrayService>();
        services.AddSingleton<BalloonService>();
        // V0.1.3 多桌宠：PaperdollService 改 Transient —— 每只纸娃娃桌宠一个实例
        // （单例只持有一份 _current/_currentHash/Flip，多只共存时后一只会顶掉前一只的外观）。
        services.AddTransient<PaperdollService>();
        services.AddSingleton<MapService>();
        // V0.1.3 多桌宠：AnimService 改 Transient —— 每只宠物一个状态机
        // （单例的单 _currentMobId / 单帧槽 / 单累加器会被 N 只宠物互相覆盖：动画串台 + 空闲随机 N 倍推进）。
        // 主宠的实例由 PetManager 绑定给 EffectLayerService（Agent 特效只驱动主宠）。
        services.AddTransient<AnimService>();
        // V0.1.3 多桌宠：宠物注册表（创建 / 移除 / 主宠切换 / 背景与 Agent 特效绑定）
        services.AddSingleton<PetManager>();
        services.AddTransient<PetWindow>();
        _serviceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            // V0.1.3 多桌宠：按配置（settings.Pets，旧配置自动迁移）创建全部桌宠；主宠作为 MainWindow
            var petManager = _serviceProvider.GetRequiredService<PetManager>();
            desktop.MainWindow = petManager.CreateAllFromConfig();

            // 自动加载 WZ
            var wzService = _serviceProvider.GetRequiredService<WzService>();
            var configService = _serviceProvider.GetRequiredService<ConfigService>();
            var eventBus = _serviceProvider.GetRequiredService<EventBus>();
            // 注入缓存（世界地图树预渲染缓存用；预构建在 WZ 加载成功后触发）
            try
            {
                var cacheManager = _serviceProvider.GetRequiredService<CacheManager>();
                wzService.SetCacheManager(cacheManager);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[App] 缓存注入: {ex.Message}"); }
            var petWindow = desktop.MainWindow as PetWindow;

            // ===== Agent 接入链装配（PLAN-agent-integration.md §1/§3）=====
            // 数据流：adapter(Hermes/CC/Codex) → EventBus(AgentStateChanged) → Aggregator
            //        → EventBus(PetReflectStateChanged) → EffectLayerService(气泡/表情)
            // Aggregator 与 EffectLayerService 在构造内已自 Subscribe，这里只 new + Start。
            // NO_AI 编译符号（Windows 纯净版打包：-p:DefineConstants=NO_AI）整块跳过——无任何
            // AI 适配器/轮询/重连，即开即用纯桌宠；正常构建不受影响。
#if !NO_AI
            try
            {
                // 读 config.AgentIntegration 配置；总开关 Enabled=false 时整块不加载（Aggregator / EffectLayer / 各 adapter）
                var ai = LoadAgentIntegration(configService);
                if (!ai.Enabled)
                {
                    Console.WriteLine("[App] Agent 集成已禁用（配置 Enabled=false）");
                }
                else
                {
                    var balloonService = _serviceProvider.GetRequiredService<BalloonService>();
                    var paperdoll = _serviceProvider.GetRequiredService<PaperdollService>();

                    // 1) 聚合器：订阅各 adapter 的 AgentStateChanged，按优先级算桌宠反映态
                    //    （构造内已 Subscribe；Start 启 staleness/Jump 衰减 DispatcherTimer）
                    _agentAggregator = new AgentStateAggregator(eventBus);
                    ApplyAgentIntegrationSettings(_agentAggregator, configService);
                    _agentAggregator.Start();

                    // 2) 特效层：订阅 PetReflectStateChanged，把反映态翻译成气泡/表情
                    //    （构造内已 Subscribe；无 Start/Stop，跟随 App 生命周期）
                    _effectLayer = new EffectLayerService(eventBus, balloonService, configService, paperdoll);
                    // 接 IActionDriver（缺口3）：AnimService 实现 IActionDriver，Run/Idle 经它切忙碌动作
                    //     （纸娃娃→walk1 / Mob→move / NPC→fallback stand）。internal set 同 assembly 可写。
                    // V0.1.3 多桌宠：AnimService 已 per-pet → Agent 特效只绑定「主宠」的实例与气泡目标；
                    //     主宠切走（移除/改设）时经 PrimaryChanged 跟随，不留悬空驱动。
                    ApplyPrimaryToEffectLayer(_effectLayer, petManager);
                    petManager.PrimaryChanged += _ =>
                    {
                        // Agent 集成被禁用时 EffectLayer 不存在 → 静默跳过，不打无谓错误
                        if (_effectLayer != null) { ApplyPrimaryToEffectLayer(_effectLayer, petManager); }
                    };

                    // 3) 按 adapter 开关实例化 adapter 并 Start
                    if (ai.HermesEnabled)
                    {
                        // HermesAdapter 传 ai（HermesProfile 生效；profile 空 = 默认 state.db）。
                        var hermes = new HermesAdapter(eventBus, ai);
                        _agentAdapters.Add(hermes);
                        hermes.Start();
                    }
                    if (ai.CCEnabled)
                    {
                        var cc = new ClaudeCodeAdapter(eventBus);
                        _agentAdapters.Add(cc);
                        cc.Start();
                    }
                    if (ai.CodexEnabled)
                    {
                        var codex = new CodexAdapter(eventBus);
                        _agentAdapters.Add(codex);
                        codex.Start();
                    }
                    if (ai.DshEnabled)
                    {
                        // DSH（DeepSeek Harness，本机 3080）：WS events.mux 纯观察 + 对话 RPC。
                        // 传 ai（DshHost/DshPort/DshWorkspace 生效；loopback 免鉴权）。
                        // Start() 内部全 catch + 指数退避重连不抛异常；连接成败经 ConnStatusCallback
                        // 回显到设置页 DSH 区状态（这里只留装配日志，避免启动期反复重连日志噪音）。
                        var dsh = new DshAdapter(eventBus, ai);
                        _agentAdapters.Add(dsh);
                        dsh.Start();
                        var dshHost = string.IsNullOrWhiteSpace(ai.DshHost) ? "127.0.0.1" : ai.DshHost.Trim();
                        var dshPort = ai.DshPort == 0 ? 3080 : ai.DshPort;
                        Console.WriteLine($"[App] DSH 接入已装配（观察模式）: ws://{dshHost}:{dshPort}/api/events.mux");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[App] Agent 接入链装配失败: {ex.Message}");
            }
#endif // !NO_AI
            var wzConfig = configService.LoadWzConfig();
            if (!string.IsNullOrEmpty(wzConfig.BaseWzPath))
            {
                try
                {
                    var (ok, err) = wzService.LoadWz(wzConfig.WzLibPath, wzConfig.BaseWzPath);
                    if (ok)
                    {
                        Console.WriteLine($"[App] WZ 自动加载成功, Mob: {wzService.ListMobIds().Count} 个, Map: {wzService.ListMapIds().Count} 个");
                        // 后台预构建世界地图树（Java familyMap/mapListCode 逻辑，预渲染到本地缓存）
                        _ = Task.Run(() =>
                        {
                            try { wzService.GetWorldMapTree(); }
                            catch (Exception ex) { Console.Error.WriteLine($"[App] 世界地图树预构建: {ex.Message}"); }
                        });
                        // V0.1.3：每只桌宠各自恢复自己的实体
                        petManager.LoadAllPets(wzService);
                    }
                    else
                    {
                        Console.Error.WriteLine($"[App] WZ 自动加载失败: {err}");
                        // 2026-08-17：加载失败默认打开设置页（用户要求「没配置 WZ 默认打开设置」；
                        // Windows 纯净版无控制台日志时也能引导配置）
                        petWindow?.OpenSettingsForWzConfig();
                        // 弹窗提示（一次启动只弹一次）
                        try
                        {
                            var failWindow = new WzLoadFailWindow(
                                WzService.GetErrorText(err ?? WzError.Unknown),
                                wzService.LastWzLibWarning);
                            failWindow.Show();
                        }
                        catch (Exception ex) { Console.Error.WriteLine($"[App] 失败弹窗: {ex.Message}"); }
                        // 缓存加载默认宠物（现有逻辑保留；V0.1.3：全部桌宠）
                        _ = _serviceProvider.GetRequiredService<SpriteService>();
                        petManager.LoadAllPets(null);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[App] WZ 加载异常: {ex.Message}");
                    try
                    {
                        var failWindow = new WzLoadFailWindow(
                            WzService.GetErrorText(WzError.Unknown),
                            wzService.LastWzLibWarning);
                        failWindow.Show();
                    }
                    catch (Exception ex2) { Console.Error.WriteLine($"[App] 异常弹窗: {ex2.Message}"); }
                    // 异常后也尝试缓存加载（V0.1.3：全部桌宠）
                    petManager.LoadAllPets(null);
                }
            }
            else
            {
                // WZ 路径未配置：默认打开设置页引导配置（2026-08-17 用户要求），并从缓存加载默认宠物
                petWindow?.OpenSettingsForWzConfig();
                petManager.LoadAllPets(null);
            }

            // 订阅 AppExit 事件来真正退出应用
            eventBus.Subscribe(EventType.AppExit, _ =>
            {
                // 先停 agent 接入链（轮询/FSW），避免退出期间还发状态事件
                try
                {
                    foreach (var ad in _agentAdapters)
                    {
                        try { ad.Stop(); } catch { }
                    }
                    _agentAdapters.Clear();
                    _agentAggregator?.Stop();
                }
                catch { }

                try
                {
                    var trayService = _serviceProvider.GetRequiredService<TrayService>();
                    trayService.HideTrayIcon();
                }
                catch { }
                // 延迟退出，让其他处理器完成清理
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { desktop.Shutdown(); }
                    catch (Exception ex) { Console.Error.WriteLine($"[App] Shutdown: {ex.Message}"); }
                });
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 把「主宠」绑成 Agent 特效的驱动 + 气泡目标（V0.1.3 多桌宠：AnimService 已 per-pet，
    /// 特效只作用于主宠；主宠切换 / 移除时重新绑定，避免指向已销毁窗口的动画实例）。
    /// </summary>
    private static void ApplyPrimaryToEffectLayer(EffectLayerService effectLayer, PetManager petManager)
    {
        try
        {
            var primary = petManager.Primary;
            effectLayer.ActionDriver = primary?.Animation;
            effectLayer.TargetPetId = primary?.PetId;
            Console.WriteLine($"[App] Agent 特效绑定主宠: {primary?.PetId ?? "(无)"}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[App] ApplyPrimaryToEffectLayer: {ex.Message}"); }
    }

    /// <summary>
    /// 读 AgentIntegration 配置（容错：读失败用默认值——默认仅 Hermes 开，PLAN §7）。
    /// </summary>
    private static AgentIntegrationConfig LoadAgentIntegration(ConfigService configService)
    {
        try
        {
            var s = configService.LoadSettings();
            return s.AgentIntegration ?? new AgentIntegrationConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[App] LoadAgentIntegration: {ex.Message}");
            return new AgentIntegrationConfig();
        }
    }

    /// <summary>
    /// 把 config.AgentIntegration 的阈值（staleness / Jump 时长）灌进 Aggregator。
    /// 轮询/静默窗阈值是 adapter 内部常量（阶段1），不在此处覆盖。
    /// </summary>
    private static void ApplyAgentIntegrationSettings(AgentStateAggregator agg, ConfigService configService)
    {
        try
        {
            var ai = LoadAgentIntegration(configService);
            if (ai.StalenessSeconds > 0) { agg.StalenessSeconds = ai.StalenessSeconds; }
            if (ai.JumpDisplaySeconds > 0) { agg.JumpDisplaySeconds = ai.JumpDisplaySeconds; }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[App] ApplyAgentIntegrationSettings: {ex.Message}");
        }
    }
}
