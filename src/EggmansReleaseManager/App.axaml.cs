using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EggmansReleaseManager.Services;
using EggmansReleaseManager.UI;
using EggmansReleaseManager.ViewModels;
using EggmansReleaseManager.Views;

namespace EggmansReleaseManager;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The Fluent DataGrid header reserves a fixed 32px for the sort arrow (Grid column MinWidth=32) even when
        // the column isn't sorted, which clips short headers like "FiLES". Let that column size to the arrow instead.
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TemplatedControl>((c, e) =>
        {
            if (c.GetType().Name != "DataGridColumnHeader") return;
            if (e.NameScope.Find<Avalonia.Controls.Shapes.Path>("SortIcon") is { Parent: Grid g } icon && g.ColumnDefinitions.Count > 1)
            {
                g.ColumnDefinitions[1].MinWidth = 0;
                icon.Margin = new Thickness(4, 0, 0, 0);
            }
        });
    }

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
