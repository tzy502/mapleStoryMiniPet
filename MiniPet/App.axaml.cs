using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MiniPet.Services;
using MiniPet.Views;

namespace MiniPet;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var petWindow = _serviceProvider.GetRequiredService<PetWindow>();
            desktop.MainWindow = petWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ConfigService>();
        services.AddSingleton<CacheManager>();
        services.AddSingleton<SpriteService>();
        services.AddTransient<AnimService>();
        services.AddTransient<PetWindow>();
    }
}