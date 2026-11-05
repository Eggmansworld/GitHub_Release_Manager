using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EggmansReleaseManager.Services;
using EggmansReleaseManager.UI;
using EggmansReleaseManager.ViewModels;
using EggmansReleaseManager.Views;

namespace EggmansReleaseManager;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var config = Stores.LoadConfig();
        ThemeManager.Apply(config.Theme, config.FontFamily, config.FontSize);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var vm = new MainViewModel(config, window);
            window.DataContext = vm;
            window.Opened += (_, _) => vm.StartIntro();
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => vm.OnShutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
