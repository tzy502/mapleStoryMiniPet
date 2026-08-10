using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Adapters;
using MiniPet.Views;

namespace MiniPet;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    // Agent 接入链（PLAN-agent-integration §1/§3）。关闭时统一 Stop，避免轮询/FSW 泄露。
    private AgentStateAggregator? _agentAggregator;
    private EffectLayerService? _effectLayer;
    private readonly List<IAgentStateAdapter> _agentAdapters = new();

    public override void OnFrameworkInitializationCompleted()
    {
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
        services.AddTransient<PetWindow>();
        _serviceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = _serviceProvider.GetRequiredService<PetWindow>();

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
            try
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

                // 3) 读 config.AgentIntegration 开关，按开关实例化 adapter 并 Start
                var ai = LoadAgentIntegration(configService);
                if (ai.HermesEnabled)
                {
                    var hermes = new HermesAdapter(eventBus);
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
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[App] Agent 接入链装配失败: {ex.Message}");
            }
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
                // WZ 路径未配置，直接从缓存加载默认宠物
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
