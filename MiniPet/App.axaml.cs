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
        services.AddSingleton<SpriteService>();
        services.AddTransient<AnimService>();
        services.AddTransient<PetWindow>();
        _serviceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = _serviceProvider.GetRequiredService<PetWindow>();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
