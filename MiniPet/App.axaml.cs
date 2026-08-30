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

    /// <summary>DI 容器静态可达点（路模块 Extractor Configure / 白盒 harness 用）。</summary>
    public static ServiceProvider? SP { get; private set; }

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
        Console.Error.WriteLine("[App] OnFrameworkInitializationCompleted 入口"); Console.Error.Flush();
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
        services.AddSingleton<PaperdollService>();
        services.AddSingleton<MapService>();
        // AnimService 用 Singleton：PetWindow 与 EffectLayerService 共享同一实例
        // （app 单 PetWindow，等价 Transient；改单例才能让 EffectLayer 的 IActionDriver 注入命中同一实例）
        services.AddSingleton<AnimService>();
        // 音乐模块 V0.2.0（F15-F17）：BASS 播放器（长生命周期单例，Bass.Init 懒初始化在首次 Prepare）+
        // 目录服务 + 播放编排服务。IMusicPlayer 与 BassMusicPlayer 指向同一实例（接口注册转发）；
        // MusicCatalogService 依赖 WzService，MusicPlayerService 依赖 WzService + MusicCatalogService +
        // IMusicPlayer + ConfigService，均可自动解析。释放：现有退出路径全部 Environment.Exit(0) 硬退
        // （BASS 流随进程回收，PLAN 步骤 8 声明；与其他单例服务同样不走容器 dispose，保持现状一致）。
        services.AddSingleton<BassMusicPlayer>();
        services.AddSingleton<IMusicPlayer>(sp => sp.GetRequiredService<BassMusicPlayer>());
        // 反向索引扫描的独立 WZ 路径来自 ConfigService——缺它则扫描永不执行（2026-08-29 白盒实证）
        services.AddSingleton<MusicCatalogService>(sp =>
            new MusicCatalogService(sp.GetRequiredService<WzService>(), sp.GetRequiredService<ConfigService>()));
        services.AddSingleton<MusicPlayerService>();
        // F18 无限之路：路窗口单例（ctor 可选参 WzService，容器注入）
        services.AddSingleton<MiniPet.Views.InfiniteRoadWindow>();
        // F18 无限之路（V0.2.1）：战斗导演 + 模式3 编排控制器（唯一编排者——订阅音乐三路事件与
        // WzReloaded，全链 UI 线程命令队列；依赖 MusicPlayerService/MusicCatalogService/ConfigService/
        // RoadCombatDirector(AnimService+PaperdollService) 均可自动解析）。路窗口（D3）经反射网关
        // 按契约签名绑定，不在 DI 显式注册（未落盘时路激活失败降级，不影响其余功能）。
        services.AddSingleton<Services.Road.RoadCombatDirector>();
        services.AddSingleton<Services.Road.RoadModeController>();
        // 音乐迷你控制条（V0.2.1 增量②）：单例窗口，构造注入 MusicPlayerService/EventBus/ConfigService
        // 自动解析；显隐自评驱动 Show/Hide，退出 Environment.Exit 硬退不走容器 dispose（与音乐服务同策略）
        services.AddSingleton<MusicMiniBarWindow>();
        services.AddTransient<PetWindow>();
        _serviceProvider = services.BuildServiceProvider();
        SP = _serviceProvider; // 路模块等静态可达点（RoadMaterialExtractor.Configure 注入用）

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            Console.Error.WriteLine("[App] MainWindow resolve 开始…"); Console.Error.Flush();
            desktop.MainWindow = _serviceProvider.GetRequiredService<PetWindow>();
            Console.Error.WriteLine("[App] MainWindow 就绪"); Console.Error.Flush();

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

            // 音乐迷你控制条（V0.2.1 增量②）：注册即实例化挂订阅（单例解析一次，之后由显隐自评
            // 决定 Show/Hide）；构造/订阅任何失败仅 stderr 不影响主流程（不崩溃不弹模态）
            try
            {
                _ = _serviceProvider.GetRequiredService<MusicMiniBarWindow>();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[App] MiniBar init: {ex.Message}"); }

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
                    _effectLayer.ActionDriver = _serviceProvider.GetRequiredService<AnimService>();

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
                        if (petWindow != null)
                            petWindow.LoadDefaultPet(wzService);
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
                        // 缓存加载默认宠物（现有逻辑保留）
                        if (petWindow != null)
                        {
                            var spriteService = _serviceProvider.GetRequiredService<SpriteService>();
                            petWindow.LoadDefaultPet(null);
                        }
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
                    // 异常后也尝试缓存加载
                    if (petWindow != null) petWindow.LoadDefaultPet(null);
                }
            }
            else
            {
                // WZ 路径未配置：默认打开设置页引导配置（2026-08-17 用户要求），并从缓存加载默认宠物
                petWindow?.OpenSettingsForWzConfig();
                if (petWindow != null) petWindow.LoadDefaultPet(null);
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
