using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using MiniPet.Services;
using MiniPet.Views;

namespace MiniPet;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

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
        services.AddTransient<AnimService>();
        services.AddTransient<PetWindow>();
        _serviceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = _serviceProvider.GetRequiredService<PetWindow>();

            // 自动加载 WZ
            var wzService = _serviceProvider.GetRequiredService<WzService>();
            var configService = _serviceProvider.GetRequiredService<ConfigService>();
            // 注入缓存（世界地图树预渲染缓存用；预构建在 WZ 加载成功后触发）
            try
            {
                var cacheManager = _serviceProvider.GetRequiredService<CacheManager>();
                wzService.SetCacheManager(cacheManager);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[App] 缓存注入: {ex.Message}"); }
            var petWindow = desktop.MainWindow as PetWindow;
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
            var eventBus = _serviceProvider.GetRequiredService<EventBus>();
            eventBus.Subscribe(EventType.AppExit, _ =>
            {
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
}
